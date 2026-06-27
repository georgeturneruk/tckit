<#
.SYNOPSIS
    Read the hardware topology (EtherCAT masters + terminals) from the
    open TwinCAT project via the XAE COM automation interface.

.DESCRIPTION
    Navigates the TIID (I/O Devices) node in the System Manager tree,
    enumerates every EtherCAT master device, then lists every terminal
    and coupler under each master.

    For each terminal the script extracts:
      - slot        — ordinal position in the bus (1-based)
      - name        — full tree name, e.g., "Box 1 (EL1008)"
      - order_number — extracted from parentheses, e.g., "EL1008"

    Does NOT trigger a physical hardware scan (ProduceXml $false, not $true),
    so no bus traffic is generated and no I/O is interrupted.

    Requires XAE to be open with a solution loaded (same constraint as all
    writer-port operations). Use the /open route to load a solution first
    when running in headless mode.

.PARAMETER ComVersion
    TwinCAT XAE DTE COM version string (default: 17.0 or COM_VERSION env).

.PARAMETER XaeMode
    'attach' (default) or 'launch'. See _TcDte.psm1 for details.
#>
param(
    [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode    = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

# Regex to extract order number from terminal tree names like:
#   "Box 1 (EL1008)"   → "EL1008"
#   "Term 2 (EK1100)"  → "EK1100"
#   "Drive 3 (AX5206)" → "AX5206"
$ORDER_RE = [regex]'\(([^)]+)\)$'

function Get-OrderNumber([string]$itemName) {
    $m = $ORDER_RE.Match($itemName)
    if ($m.Success) { $m.Groups[1].Value.Trim() } else { '' }
}

function Get-TerminalSlot([string]$itemName) {
    # Extract the slot/ordinal from names like "Box 1", "Term 3", "Drive 5"
    $m = [regex]'^(?:Box|Term|Drive|Module|Slot|Device)\s+(\d+)'.Match($itemName)
    if ($m.Success) { [int]$m.Groups[1].Value } else { 0 }
}

function Is-EtherCatMaster([object]$deviceItem) {
    # TwinCAT EtherCAT master devices have "EtherCAT" in their name
    $name = try { $deviceItem.Name } catch { '' }
    return ($name -match 'EtherCAT' -or $name -match 'EL6695' -or $name -match 'EK9300')
}

try {
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    # Get the first available System Manager (no PLC name needed for I/O tree)
    $managers = Get-TcSysManagers -Dte $dte -MaxAttempts 5 -DelayMs 400
    if (-not $managers -or $managers.Count -eq 0) {
        return @{ success = $false; error = 'No TwinCAT System Manager found. Ensure XAE is open with a solution loaded.' }
    }
    $sm = $managers[0]

    # Navigate to TIID (I/O Devices)
    $tiid = $null
    try {
        $tiid = Invoke-WithComRetry { $sm.LookupTreeItem('TIID') }
    } catch {
        return @{ success = $false; error = "Failed to access I/O devices tree (TIID): $($_.Exception.Message)" }
    }
    if (-not $tiid) {
        return @{ success = $false; error = 'TIID node not found. Ensure a TwinCAT solution is loaded in XAE.' }
    }

    $segments   = @()
    $timestamp  = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')

    # Enumerate devices under TIID
    $deviceCount = 0
    try { $deviceCount = $tiid.ChildCount } catch {}

    for ($d = 1; $d -le $deviceCount; $d++) {
        $device = $null
        try { $device = $tiid.Child($d) } catch { continue }
        if (-not $device) { continue }

        $deviceName = ''
        try { $deviceName = $device.Name } catch {}

        if (-not (Is-EtherCatMaster $device)) { continue }

        $terminals   = @()
        $termCount   = 0
        try { $termCount = $device.ChildCount } catch {}

        for ($t = 1; $t -le $termCount; $t++) {
            $term = $null
            try { $term = $device.Child($t) } catch { continue }
            if (-not $term) { continue }

            $termName = ''
            try { $termName = $term.Name } catch { continue }

            $orderNumber = Get-OrderNumber $termName
            $slot        = Get-TerminalSlot $termName
            if ($slot -eq 0) { $slot = $t }  # fallback to child index

            $terminals += @{
                slot         = $slot
                name         = $termName
                order_number = $orderNumber
            }
        }

        $segments += @{
            master_name = $deviceName
            terminals   = $terminals
        }
    }

    return @{
        success         = $true
        segments        = $segments
        scan_timestamp  = $timestamp
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
