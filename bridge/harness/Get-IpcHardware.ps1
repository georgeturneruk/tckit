#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Read IPC hardware diagnostics from a running TwinCAT system via MDP.

.DESCRIPTION
    Connects to AMS port 10000 (SystemService) and reads all MDP (Module
    Description Protocol) modules found on the target IPC.  Returns:
      - TwinCAT version (major.minor.build)
      - CPU temperature, utilisation %, clock frequency
      - Memory total and free (MB)
      - Fan speeds (RPM per fan)
      - Network adapters (MAC address, IPv4)
      - UPS status (battery %, power OK, fail count)

    Uses the same TcKit.TcAdsHelper type defined by Get-EtherCatStatus.ps1
    (compiled once per PS session; redefinition is guarded).

    MDP index groups (all at port 10000, IG 0xF302):
      Module list count:   IO = 0xF0200000 (ushort)
      Module i descriptor: IO = 0xF0200000 + i (uint; high=type, low=mdpId)
      Per-module property: IO = (mdpId << 20 | 0x80010000) + subOffset

    Module type constants:
      0x0002 = NIC   0x0008 = TwinCAT  0x000B = CPU
      0x000C = Memory  0x001B = Fan     0x001E = UPS

.PARAMETER TargetAmsId
    AMS Net ID of the TwinCAT system.  Falls back to TARGET_AMS_ID env var.
#>
param(
    [string]$TargetAmsId = $env:TARGET_AMS_ID
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

# ---------------------------------------------------------------------------
# TcKit.TcAdsHelper — compile once per session (defined in Get-EtherCatStatus)
# ---------------------------------------------------------------------------

if (-not ([System.Management.Automation.PSTypeName]'TcKit.TcAdsHelper').Type) {
    $csharp = @'
using System;
using System.Runtime.InteropServices;

namespace TcKit {
    [StructLayout(LayoutKind.Sequential)]
    public struct AmsAddr {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] NetId;
        public ushort Port;

        public AmsAddr(string netIdStr, ushort port) {
            var parts = netIdStr.Split('.');
            NetId = new byte[] {
                byte.Parse(parts[0]), byte.Parse(parts[1]),
                byte.Parse(parts[2]), byte.Parse(parts[3]),
                byte.Parse(parts[4]), byte.Parse(parts[5])
            };
            Port = port;
        }
    }

    public static class TcAdsHelper {
        private const string DLL = "TcAdsDll.dll";

        [DllImport(DLL, EntryPoint = "AdsPortOpenEx")]
        private static extern long AdsPortOpenEx();

        [DllImport(DLL, EntryPoint = "AdsPortCloseEx")]
        private static extern uint AdsPortCloseEx(long nPort);

        [DllImport(DLL, EntryPoint = "AdsSyncReadReqEx2")]
        private static extern uint AdsSyncReadReqEx2(
            long nPort,
            ref AmsAddr pServerAddr,
            uint nIndexGroup,
            uint nIndexOffset,
            uint nLength,
            [Out] byte[] pData,
            ref uint pcbReturn);

        public static byte[] Read(string netId, ushort port, uint ig, uint io, int length) {
            long nPort = AdsPortOpenEx();
            try {
                var addr  = new AmsAddr(netId, port);
                var data  = new byte[length];
                uint cbRet = 0;
                uint rc = AdsSyncReadReqEx2(nPort, ref addr, ig, io, (uint)length, data, ref cbRet);
                if (rc != 0)
                    throw new Exception(string.Format("ADS error 0x{0:X8} reading {1}:{2} IG=0x{3:X8} IO=0x{4:X8}", rc, netId, port, ig, io));
                return data;
            } finally {
                AdsPortCloseEx(nPort);
            }
        }

        public static bool CanConnect(string netId, ushort port) {
            try { Read(netId, port, 0x00000006, 0x00000000, 2); return true; }
            catch { return false; }
        }
    }
}
'@
    Add-Type -TypeDefinition $csharp -Language CSharp -ErrorAction Stop
}

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

$SYSSERVICE_PORT = [ushort]10000

$IG_MDP = [uint32]0xF302

$IO_MODULE_COUNT = [uint32]0xF0200000

# MDP module type constants
$TYPE_NIC      = 0x0002
$TYPE_TWINCAT  = 0x0008
$TYPE_CPU      = 0x000B
$TYPE_MEMORY   = 0x000C
$TYPE_FAN      = 0x001B
$TYPE_UPS      = 0x001E

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Read-UInt16LE([byte[]]$b, [int]$o) {
    [uint16]([uint16]$b[$o] -bor ([uint16]$b[$o + 1] -shl 8))
}

function Read-UInt32LE([byte[]]$b, [int]$o) {
    [uint32]([uint32]$b[$o] -bor ([uint32]$b[$o + 1] -shl 8) `
        -bor ([uint32]$b[$o + 2] -shl 16) -bor ([uint32]$b[$o + 3] -shl 24))
}

function Read-Int16LE([byte[]]$b, [int]$o) {
    [int16](Read-UInt16LE $b $o)
}

function Get-MdpSubIndex([uint16]$mdpId) {
    # TwinSharp formula: (mdpId << 20) | 0x80010000
    [uint32](([uint32]$mdpId -shl 20) -bor [uint32]0x80010000)
}

function Read-MdpUInt32([string]$netId, [uint16]$mdpId, [uint32]$subOffset) {
    $io = [uint32]((Get-MdpSubIndex $mdpId) + $subOffset)
    $b = [TcKit.TcAdsHelper]::Read($netId, $SYSSERVICE_PORT, $IG_MDP, $io, 4)
    Read-UInt32LE $b 0
}

function Read-MdpUInt16([string]$netId, [uint16]$mdpId, [uint32]$subOffset) {
    $io = [uint32]((Get-MdpSubIndex $mdpId) + $subOffset)
    $b = [TcKit.TcAdsHelper]::Read($netId, $SYSSERVICE_PORT, $IG_MDP, $io, 2)
    Read-UInt16LE $b 0
}

function Read-MdpInt16([string]$netId, [uint16]$mdpId, [uint32]$subOffset) {
    $io = [uint32]((Get-MdpSubIndex $mdpId) + $subOffset)
    $b = [TcKit.TcAdsHelper]::Read($netId, $SYSSERVICE_PORT, $IG_MDP, $io, 2)
    Read-Int16LE $b 0
}

function Read-MdpByte([string]$netId, [uint16]$mdpId, [uint32]$subOffset) {
    $io = [uint32]((Get-MdpSubIndex $mdpId) + $subOffset)
    $b = [TcKit.TcAdsHelper]::Read($netId, $SYSSERVICE_PORT, $IG_MDP, $io, 1)
    [int]$b[0]
}

function Read-MdpString([string]$netId, [uint16]$mdpId, [uint32]$subOffset) {
    $io = [uint32]((Get-MdpSubIndex $mdpId) + $subOffset)
    $b = [TcKit.TcAdsHelper]::Read($netId, $SYSSERVICE_PORT, $IG_MDP, $io, 80)
    $nullIdx = [Array]::IndexOf($b, [byte]0)
    if ($nullIdx -lt 0) { $nullIdx = $b.Length }
    [System.Text.Encoding]::ASCII.GetString($b, 0, $nullIdx).Trim()
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

try {
    if (-not $TargetAmsId) {
        return @{ success = $false; error = 'TargetAmsId required.' }
    }

    # Read module count
    $countBytes = [TcKit.TcAdsHelper]::Read($TargetAmsId, $SYSSERVICE_PORT, $IG_MDP, $IO_MODULE_COUNT, 2)
    $moduleCount = [int](Read-UInt16LE $countBytes 0)

    # Enumerate modules
    $tcVer   = $null
    $cpu     = $null
    $memory  = $null
    $fans    = @()
    $nics    = @()
    $ups     = $null

    for ($i = 0; $i -le $moduleCount; $i++) {
        $io = [uint32]($IO_MODULE_COUNT + [uint32]$i)
        try {
            $modBytes = [TcKit.TcAdsHelper]::Read($TargetAmsId, $SYSSERVICE_PORT, $IG_MDP, $io, 4)
        } catch { continue }
        $modValue = Read-UInt32LE $modBytes 0
        $mdpType = [uint16](($modValue -shr 16) -band 0xFFFF)
        $mdpId   = [uint16]($modValue -band 0xFFFF)

        switch ($mdpType) {
            $TYPE_TWINCAT {
                try {
                    $major = Read-MdpUInt16 $TargetAmsId $mdpId 0x01
                    $minor = Read-MdpUInt16 $TargetAmsId $mdpId 0x02
                    $build = Read-MdpUInt16 $TargetAmsId $mdpId 0x03
                    $tcVer = "$major.$minor.$build"
                } catch {}
            }
            $TYPE_CPU {
                $freq   = $null
                $usage  = $null
                $tempC  = $null
                try { $freq  = [int](Read-MdpUInt32 $TargetAmsId $mdpId 0x01) / 1000000 } catch {}
                try { $usage = [int](Read-MdpUInt16 $TargetAmsId $mdpId 0x02) } catch {}
                try { $tempC = [int](Read-MdpInt16 $TargetAmsId $mdpId 0x03) } catch {}
                if ($null -ne $freq -or $null -ne $usage) {
                    $cpu = @{
                        temperature_c  = $tempC
                        usage_pct      = if ($null -ne $usage) { $usage } else { 0 }
                        frequency_mhz  = if ($null -ne $freq)  { $freq  } else { 0 }
                    }
                }
            }
            $TYPE_MEMORY {
                try {
                    $total = [int](Read-MdpUInt32 $TargetAmsId $mdpId 0x01)
                    $free  = [int](Read-MdpUInt32 $TargetAmsId $mdpId 0x02)
                    $memory = @{ total_mb = $total; free_mb = $free }
                } catch {}
            }
            $TYPE_FAN {
                try {
                    $rpm = [int](Read-MdpUInt32 $TargetAmsId $mdpId 0x01)
                    $fans += @{ rpm = $rpm }
                } catch {}
            }
            $TYPE_NIC {
                try {
                    $mac  = Read-MdpString $TargetAmsId $mdpId 0x01
                    $ipv4 = Read-MdpString $TargetAmsId $mdpId 0x02
                    if ($mac -or $ipv4) {
                        $nics += @{ mac = $mac; ipv4 = $ipv4 }
                    }
                } catch {}
            }
            $TYPE_UPS {
                try {
                    $batPct    = Read-MdpByte $TargetAmsId $mdpId 0x0A  # battery capacity %
                    $batStatus = Read-MdpByte $TargetAmsId $mdpId 0x09  # battery status byte
                    $pwrStatus = Read-MdpByte $TargetAmsId $mdpId 0x07  # power status byte
                    $failCount = [int](Read-MdpUInt32 $TargetAmsId $mdpId 0x0D)
                    $ups = @{
                        battery_pct      = $batPct
                        power_ok         = ($pwrStatus -eq 0)
                        battery_ok       = ($batStatus -eq 0)
                        power_fail_count = $failCount
                    }
                } catch {}
            }
        }
    }

    return @{
        success          = $true
        twincat_version  = $tcVer
        cpu              = $cpu
        memory           = $memory
        fans             = $fans
        nics             = $nics
        ups              = $ups
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
