#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Read EtherCAT master state and slave table from a running TwinCAT system.

.DESCRIPTION
    Connects to the target's EtherCAT master AMS port (0xFFFF / 65535) and
    reads raw ADS index groups to build a structured snapshot of:
      - Master device-state flags (link error, watchdog, DC sync)
      - Configured slave count
      - Per-slave state machine (INIT / PREOP / SAFEOP / OP / ERROR)
      - Per-slave identity (VendorId, ProductCode, RevisionNo, SerialNo)
      - Per-slave CRC error counters (ports A, B, C, D)

    Uses TcAdsDll.dll (native Win32, ships with TwinCAT 3) via inline C# for
    raw index-group reads.  TcXaeMgmt is imported only to satisfy the #Requires
    declaration (which the bridge health check already validates).

    When ListMastersOnly=true, probes port 65535 and returns just the master
    list without the full slave read.

.PARAMETER TargetAmsId
    AMS Net ID of the TwinCAT system (e.g. "192.168.1.100.1.1").
    Falls back to TARGET_AMS_ID env var.

.PARAMETER MasterNetId
    AMS Net ID of the EtherCAT master.  Defaults to TargetAmsId (the usual
    single-master layout where master and system share the same AMS node).

.PARAMETER ListMastersOnly
    When $true, only probe for the master and skip slave enumeration.
#>
param(
    [string]$TargetAmsId    = $env:TARGET_AMS_ID,
    [string]$MasterNetId    = '',
    [bool]  $ListMastersOnly = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

# ---------------------------------------------------------------------------
# Load TcAdsDll.dll P/Invoke helper (once per PS session via type cache)
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

        /// <summary>Read bytes from an ADS index group/offset.</summary>
        /// <returns>Byte array of <paramref name="length"/> bytes on success.</returns>
        /// <exception cref="Exception">Thrown on ADS error code != 0.</exception>
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

$ECMASTER_PORT = [ushort]65535   # 0xFFFF

# EtherCAT index groups (at ECMASTER_PORT)
$IG_SLAVE_COUNT    = [uint32]0x00000006   # UINT16: number of configured slaves
$IG_SLAVE_STATES   = [uint32]0x00000009   # array of ST_EcSlaveState (4 bytes each)
$IG_SLAVE_CFG_BASE = [uint32]0x80000100   # per-slave config block (80 bytes); +0x10000 per address
$IG_CRC_ERRORS     = [uint32]0x00000012   # array of ST_EcCrcErrorEx (16 bytes each)
$IG_MASTER_STATE   = [uint32]0x00000045   # DWORD device-state flags

# EcDeviceState bits
$EC_STATE_MASK       = 0x0F  # lower nibble = state
$EC_STATE_INIT       = 0x01
$EC_STATE_PREOP      = 0x02
$EC_STATE_BOOTSTRAP  = 0x03
$EC_STATE_SAFEOP     = 0x04
$EC_STATE_OP         = 0x08
$EC_ERROR_BIT        = 0x10

# EcLinkState bits (second byte of the 4-byte slave state struct)
$EC_LINK_PORT_A = 0x01  # link up on port A
$EC_LINK_PORT_B = 0x02

# Master state flags (IG 0x45)
$MASTER_LINK_ERROR    = 0x0001
$MASTER_IO_LOCKED     = 0x0002
$MASTER_WATCHDOG      = 0x0020
$MASTER_DC_OUT_SYNC   = 0x1000

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function ConvertTo-EcStateName([byte]$stateVal) {
    $s = $stateVal -band $EC_STATE_MASK
    $err = ($stateVal -band $EC_ERROR_BIT) -ne 0
    $name = switch ($s) {
        $EC_STATE_INIT      { 'INIT' }
        $EC_STATE_PREOP     { 'PREOP' }
        $EC_STATE_BOOTSTRAP { 'BOOTSTRAP' }
        $EC_STATE_SAFEOP    { 'SAFEOP' }
        $EC_STATE_OP        { 'OP' }
        default             { 'UNKNOWN' }
    }
    if ($err) { "$name+ERROR" } else { $name }
}

function Read-UInt16LE([byte[]]$bytes, [int]$offset) {
    [uint16]([uint16]$bytes[$offset] -bor ([uint16]$bytes[$offset + 1] -shl 8))
}

function Read-UInt32LE([byte[]]$bytes, [int]$offset) {
    [uint32]([uint32]$bytes[$offset] -bor ([uint32]$bytes[$offset + 1] -shl 8) `
        -bor ([uint32]$bytes[$offset + 2] -shl 16) -bor ([uint32]$bytes[$offset + 3] -shl 24))
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

try {
    if (-not $TargetAmsId) {
        return @{ success = $false; error = 'TargetAmsId required.' }
    }

    $masterNetId = if ($MasterNetId) { $MasterNetId } else { $TargetAmsId }

    # --- Probe: can we reach the EtherCAT master? ---
    $canConnect = [TcKit.TcAdsHelper]::CanConnect($masterNetId, $ECMASTER_PORT)
    if (-not $canConnect) {
        if ($ListMastersOnly) {
            return @{ success = $true; masters = @() }
        }
        return @{ success = $false; error = "No EtherCAT master found at $masterNetId`:$ECMASTER_PORT. Ensure TwinCAT is running and the AMS route is reachable." }
    }

    # --- Return master list only (fast path) ---
    if ($ListMastersOnly) {
        return @{
            success = $true
            masters = @(
                @{
                    net_id = $masterNetId
                    name   = 'EtherCAT Master'
                    port   = $ECMASTER_PORT
                }
            )
        }
    }

    # --- Slave count ---
    $slaveCountBytes = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $IG_SLAVE_COUNT, 0, 2)
    $slaveCount = [int](Read-UInt16LE $slaveCountBytes 0)

    if ($slaveCount -eq 0) {
        $masterStateBytes = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $IG_MASTER_STATE, 0, 4)
        $masterFlags = [int](Read-UInt32LE $masterStateBytes 0)
        return @{
            success = $true
            master  = @{
                state_flags       = $masterFlags
                link_error        = (($masterFlags -band $MASTER_LINK_ERROR) -ne 0)
                io_locked         = (($masterFlags -band $MASTER_IO_LOCKED) -ne 0)
                watchdog_triggered = (($masterFlags -band $MASTER_WATCHDOG) -ne 0)
                dc_out_of_sync    = (($masterFlags -band $MASTER_DC_OUT_SYNC) -ne 0)
            }
            slaves  = @()
        }
    }

    # --- Slave states (4 bytes each: 2-byte state + 2-byte link) ---
    $stateBytes = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $IG_SLAVE_STATES, 0, $slaveCount * 4)

    # --- CRC errors (16 bytes each: 4 × UINT32 for ports A-D) ---
    $crcBytes = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $IG_CRC_ERRORS, 0, $slaveCount * 16)

    # --- Master device state ---
    $masterStateBytes = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $IG_MASTER_STATE, 0, 4)
    $masterFlags = [int](Read-UInt32LE $masterStateBytes 0)

    # --- Per-slave identity (config block, 80 bytes each at base + addr*0x10000) ---
    $slaves = @()
    for ($i = 0; $i -lt $slaveCount; $i++) {
        $addr = $i + 1  # EtherCAT addresses are 1-based

        # State
        $stateOff  = $i * 4
        $stateVal  = $stateBytes[$stateOff]
        $linkVal   = $stateBytes[$stateOff + 2]
        $stateName = ConvertTo-EcStateName $stateVal
        $linkOk    = ($linkVal -band $EC_LINK_PORT_A) -ne 0

        # CRC counters
        $crcOff = $i * 16
        $crcA = [int](Read-UInt32LE $crcBytes $crcOff)
        $crcB = [int](Read-UInt32LE $crcBytes ($crcOff + 4))
        $crcC = [int](Read-UInt32LE $crcBytes ($crcOff + 8))
        $crcD = [int](Read-UInt32LE $crcBytes ($crcOff + 12))

        # Identity: IG = $IG_SLAVE_CFG_BASE + addr * 0x10000, offset 0, 80 bytes
        $cfgIg = [uint32]($IG_SLAVE_CFG_BASE + [uint32]($addr * 0x10000))
        $vendorId    = 0
        $productCode = 0
        $revision    = 0
        $serial      = 0
        try {
            $cfgBytes    = [TcKit.TcAdsHelper]::Read($masterNetId, $ECMASTER_PORT, $cfgIg, 0, 80)
            $vendorId    = [int](Read-UInt32LE $cfgBytes 0)
            $productCode = [int](Read-UInt32LE $cfgBytes 4)
            $revision    = [int](Read-UInt32LE $cfgBytes 8)
            $serial      = [int](Read-UInt32LE $cfgBytes 12)
        } catch {
            # Identity read failure is non-fatal; slave may be offline
        }

        $slaves += @{
            address      = $addr
            name         = "Slave $addr"
            vendor_id    = $vendorId
            product_code = $productCode
            revision     = $revision
            serial       = $serial
            state        = $stateName
            link_ok      = $linkOk
            crc_errors_a = $crcA
            crc_errors_b = $crcB
            crc_errors_c = $crcC
            crc_errors_d = $crcD
        }
    }

    return @{
        success = $true
        master  = @{
            state_flags        = $masterFlags
            link_error         = (($masterFlags -band $MASTER_LINK_ERROR) -ne 0)
            io_locked          = (($masterFlags -band $MASTER_IO_LOCKED) -ne 0)
            watchdog_triggered = (($masterFlags -band $MASTER_WATCHDOG) -ne 0)
            dc_out_of_sync     = (($masterFlags -band $MASTER_DC_OUT_SYNC) -ne 0)
        }
        slaves  = $slaves
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
