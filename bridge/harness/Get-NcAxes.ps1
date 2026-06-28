#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Read NC axis list and live state from a running TwinCAT system.

.DESCRIPTION
    Connects to AMS port 500 (R0_NC) and reads all configured axes from
    the NC Ring0 manager.  For each axis returns:
      - Id, Name
      - ErrorCode, DelayedErrorCode
      - ActualPosition, ActualVelocity, LagErrorPosition
      - Derived state name: Standstill / Moving / Error

    ADS index groups (all at port 500):
      Ring0 axis count:    IG=0x1100  IO=0x03  (uint32)
      Ring0 axis IDs:      IG=0x1100  IO=0x33  (uint32[])
      Axis name:           IG=0x4000+id  IO=0x02        (string 80b)
      Axis error:          IG=0x4100+id  IO=0x01        (uint32)
      Delayed error:       IG=0x4100+id  IO=0x29        (uint32)
      Actual position:     IG=0x4100+id  IO=0x10002     (double 8b)
      Actual velocity:     IG=0x4100+id  IO=0x10005     (double 8b)
      Lag error:           IG=0x4100+id  IO=0x2000D     (double 8b)

.PARAMETER TargetAmsId
    AMS Net ID of the TwinCAT system.  Falls back to TARGET_AMS_ID env var.

.PARAMETER AxisId
    When specified, return only this axis.  Omit to return all axes.
#>
param(
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [object]$AxisId      = $null
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

# ---------------------------------------------------------------------------
# TcKit.TcAdsHelper — compiled once per session
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

$NC_PORT = [ushort]500  # R0_NC

# Ring0 index group
$IG_RING0 = [uint32]0x1100
$IO_AXIS_COUNT = [uint32]0x03
$IO_AXIS_IDS   = [uint32]0x33

# Velocity threshold to detect motion (units/s)
$MOTION_THRESHOLD = 0.001

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Read-UInt32LE([byte[]]$b, [int]$o) {
    [uint32]([uint32]$b[$o] -bor ([uint32]$b[$o+1] -shl 8) `
        -bor ([uint32]$b[$o+2] -shl 16) -bor ([uint32]$b[$o+3] -shl 24))
}

function Read-Double([byte[]]$b, [int]$o) {
    [System.BitConverter]::ToDouble($b, $o)
}

function Read-NcString([string]$netId, [uint32]$ig, [uint32]$io) {
    $b = [TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $ig, $io, 80)
    $null_ = [Array]::IndexOf($b, [byte]0)
    if ($null_ -lt 0) { $null_ = $b.Length }
    [System.Text.Encoding]::ASCII.GetString($b, 0, $null_).Trim()
}

function Read-AxisState([string]$netId, [uint32]$axisId) {
    $stateIg = [uint32](0x4100 + $axisId)
    $paramIg = [uint32](0x4000 + $axisId)

    $name     = ''
    $errCode  = [uint32]0
    $delErr   = [uint32]0
    $pos      = [double]0.0
    $vel      = [double]0.0
    $lagErr   = [double]0.0

    try { $name    = Read-NcString $netId $paramIg 0x02 } catch {}
    try { $errCode = Read-UInt32LE ([TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $stateIg, 0x01, 4)) 0 } catch {}
    try { $delErr  = Read-UInt32LE ([TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $stateIg, 0x29, 4)) 0 } catch {}
    try { $pos     = Read-Double   ([TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $stateIg, 0x10002, 8)) 0 } catch {}
    try { $vel     = Read-Double   ([TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $stateIg, 0x10005, 8)) 0 } catch {}
    try { $lagErr  = Read-Double   ([TcKit.TcAdsHelper]::Read($netId, $NC_PORT, $stateIg, 0x2000D, 8)) 0 } catch {}

    $stateName = if ($errCode -ne 0) {
        'Error'
    } elseif ([Math]::Abs($vel) -gt $MOTION_THRESHOLD) {
        'Moving'
    } else {
        'Standstill'
    }

    return @{
        id                  = [int]$axisId
        name                = $name
        error_code          = [int]$errCode
        delayed_error_code  = [int]$delErr
        position            = $pos
        velocity            = $vel
        lag_error           = $lagErr
        state_name          = $stateName
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

try {
    if (-not $TargetAmsId) {
        return @{ success = $false; error = 'TargetAmsId required.' }
    }

    # --- Single-axis fast path ---
    if ($null -ne $AxisId -and $AxisId -ne '') {
        $id = [uint32]$AxisId
        $axis = Read-AxisState $TargetAmsId $id
        if ($axis.error_code -ne 0 -and -not $axis.name) {
            # Non-existent axis: ADS read likely failed, name is empty and error is non-zero
            return @{ success = $false; error = "Axis $id not found or not reachable." }
        }
        return @{ success = $true; axes = @($axis) }
    }

    # --- Enumerate all axes ---
    $countBytes = [TcKit.TcAdsHelper]::Read($TargetAmsId, $NC_PORT, $IG_RING0, $IO_AXIS_COUNT, 4)
    $axisCount = [int](Read-UInt32LE $countBytes 0)

    if ($axisCount -eq 0) {
        return @{ success = $true; axes = @() }
    }

    # Read axis ID array
    $idBytes = [TcKit.TcAdsHelper]::Read($TargetAmsId, $NC_PORT, $IG_RING0, $IO_AXIS_IDS, ($axisCount * 4))
    $axisIds = @()
    for ($i = 0; $i -lt $axisCount; $i++) {
        $axisIds += Read-UInt32LE $idBytes ($i * 4)
    }

    $axes = @()
    foreach ($id in $axisIds) {
        try {
            $axes += Read-AxisState $TargetAmsId $id
        } catch {
            # Non-fatal: skip axes that can't be read
        }
    }

    return @{ success = $true; axes = $axes }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
