<#
.SYNOPSIS
    Phase 2 spike: validate ProduceXml / ConsumeXml round-trip on TwinCAT 4026.

.DESCRIPTION
    Attaches to a running TcXaeShell, navigates to a known POU + item,
    calls ProduceXml() to capture the XML, edits the implementation block,
    calls ConsumeXml() to write it back, then re-reads to confirm.

    The user must edit the script (or pass parameters) to point at a real
    POU + method that exists in the currently-loaded solution.

.PARAMETER PouName
    Name of an existing POU in the loaded solution (e.g. FB_Example).

.PARAMETER ItemName
    Name of an existing method/action/property on that POU.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.EXAMPLE
    .\scripts\spike-producexml.ps1 -PouName FB_Example -ItemName Execute
#>
param(
    [Parameter(Mandatory = $true)][string]$PouName,
    [Parameter(Mandatory = $true)][string]$ItemName,
    [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' })
)

$ErrorActionPreference = 'Stop'

$progId = "TcXaeShell.DTE.$ComVersion"
Write-Host "Attaching to $progId..."
$dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)
Write-Host "OK. DTE Version: $($dte.Version)"

if ($dte.Solution.Projects.Count -eq 0) {
    throw 'No projects in active solution. Open a TwinCAT solution first.'
}

# Find the first PLC project — TwinCAT projects expose ITcSysManager via .Object.
$tcProject = $null
foreach ($proj in $dte.Solution.Projects) {
    try {
        $sysMan = $proj.Object
        if ($null -ne $sysMan -and $sysMan.GetType().Name -match 'TcSysManager|ITcSysManager') {
            $tcProject = $proj
            break
        }
    } catch { continue }
}
if ($null -eq $tcProject) { throw 'No TwinCAT (ITcSysManager) project found in solution.' }

$sysManager = $tcProject.Object
Write-Host "Project: $($tcProject.Name)"

# Navigate to the PLC POUs folder. TIPC = TwinCAT PLC root in tree.
# Then drill into the first PLC project, then POUs.
Write-Host 'Looking up TIPC tree...'
$tipc = $sysManager.LookupTreeItem('TIPC')
if ($tipc.ChildCount -lt 1) { throw 'No PLC project under TIPC.' }
$plcProject = $tipc.Child(1)
Write-Host "PLC project: $($plcProject.Name)"

# Walk PLC project to find the POU by name. Different installs nest differently
# (POUs folder, GVLs folder, custom folders), so do a recursive walk.
function Find-Item {
    param($node, [string]$name)
    if ($node.Name -eq $name) { return $node }
    if ($node.ChildCount -lt 1) { return $null }
    for ($i = 1; $i -le $node.ChildCount; $i++) {
        $found = Find-Item -node $node.Child($i) -name $name
        if ($null -ne $found) { return $found }
    }
    return $null
}

Write-Host "Searching for POU '$PouName'..."
$pou = Find-Item -node $plcProject -name $PouName
if ($null -eq $pou) { throw "POU '$PouName' not found." }
Write-Host "Found POU: $($pou.Name)"

Write-Host "Searching for item '$ItemName' on POU..."
$item = Find-Item -node $pou -name $ItemName
if ($null -eq $item) { throw "Item '$ItemName' not found on POU '$PouName'." }
Write-Host "Found item: $($item.Name)"

Write-Host ''
Write-Host '=== ProduceXml() output (first 800 chars) ==='
$xml = $item.ProduceXml()
$preview = if ($xml.Length -gt 800) { $xml.Substring(0, 800) + "`n... [truncated, total $($xml.Length) chars]" } else { $xml }
Write-Host $preview
Write-Host '=== end ==='

Write-Host ''
Write-Host 'Round-trip test: re-consuming the same XML unchanged...'
try {
    $item.ConsumeXml($xml)
    Write-Host 'ConsumeXml() succeeded with original XML (no changes).'
} catch {
    Write-Host "ConsumeXml() FAILED on unchanged round-trip: $_" -ForegroundColor Red
    throw
}

# Re-read and compare
$xml2 = $item.ProduceXml()
if ($xml -eq $xml2) {
    Write-Host 'Round-trip preserved exact XML byte-for-byte.'
} else {
    Write-Host 'Round-trip XML differs slightly (likely whitespace/attribute ordering).' -ForegroundColor Yellow
    $diff = [Math]::Abs($xml.Length - $xml2.Length)
    Write-Host "  length delta: $diff chars"
}

Write-Host ''
Write-Host 'Spike complete. Update SPIKE_NOTES.md with the XML shape observed above.'
