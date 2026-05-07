<#
.SYNOPSIS
    Phase 2 spike: validate CreateChild() kind constants + GUID assignment.

.DESCRIPTION
    Creates a throwaway FB called FB_SpikeTest with one method called SpikeMethod
    in the loaded TwinCAT solution, captures the assigned GUID, and prints the
    resulting XML so we can confirm the kind constants used by add_pou and
    add_method are correct.

    SAFE TO RUN — does not modify any existing items. After running, manually
    delete FB_SpikeTest from the project tree. (Or pass -CleanUp to attempt
    automatic deletion.)

.PARAMETER FbKind
    Kind code for FUNCTION_BLOCK. Default 604. Override if spike shows otherwise.

.PARAMETER MethodKind
    Kind code for METHOD. Default 603. Override if spike shows otherwise.

.PARAMETER CleanUp
    If set, attempts to delete FB_SpikeTest after capturing findings.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.EXAMPLE
    .\scripts\spike-createchild.ps1
    .\scripts\spike-createchild.ps1 -CleanUp
#>
param(
    [int]$FbKind = 604,
    [int]$MethodKind = 603,
    [switch]$CleanUp,
    [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' })
)

$ErrorActionPreference = 'Stop'

$progId = "TcXaeShell.DTE.$ComVersion"
Write-Host "Attaching to $progId..."
$dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)

if ($dte.Solution.Projects.Count -eq 0) { throw 'No projects loaded.' }

# Find first TwinCAT project + its first PLC project under TIPC.
$tcProject = $null
foreach ($proj in $dte.Solution.Projects) {
    try {
        $sm = $proj.Object
        if ($null -ne $sm -and $sm.GetType().Name -match 'TcSysManager|ITcSysManager') {
            $tcProject = $proj; break
        }
    } catch { continue }
}
if ($null -eq $tcProject) { throw 'No TwinCAT project found.' }

$sysManager = $tcProject.Object
$plcProject = $sysManager.LookupTreeItem('TIPC').Child(1)
Write-Host "PLC project: $($plcProject.Name)"

# Find a POUs folder. Many projects have a folder literally named "POUs"; if not,
# attach the new FB directly to the PLC project root.
$pousFolder = $null
for ($i = 1; $i -le $plcProject.ChildCount; $i++) {
    $c = $plcProject.Child($i)
    if ($c.Name -eq 'POUs') { $pousFolder = $c; break }
}
$parent = if ($null -ne $pousFolder) { $pousFolder } else { $plcProject }
Write-Host "Adding FB_SpikeTest under: $($parent.Name)"

# CreateChild signature varies by interface. Common form:
#   CreateChild(name, subType, beforeNode, data)
# For PLC items, subType is the kind constant. data is usually empty string.
$newFb = $null
try {
    $newFb = $parent.CreateChild('FB_SpikeTest', $FbKind, '', '')
    Write-Host "CreateChild(FB) succeeded with kind=$FbKind"
} catch {
    Write-Host "CreateChild(FB) FAILED with kind=$FbKind : $_" -ForegroundColor Red
    Write-Host 'Try varying the kind constant. Documented values to test: 604, 605, 602.' -ForegroundColor Yellow
    throw
}

# Try to read the GUID from the new tree item.
try {
    $guid = $newFb.GUID
    Write-Host "FB GUID: $guid"
} catch {
    try {
        $xml = $newFb.ProduceXml()
        if ($xml -match 'GUID="?\{?([0-9a-fA-F-]{36})\}?"?') {
            Write-Host "FB GUID (from XML): $($Matches[1])"
        } else {
            Write-Host 'No GUID property exposed; not found in ProduceXml output either.' -ForegroundColor Yellow
        }
    } catch {
        Write-Host "Could not read GUID: $_" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '=== FB ProduceXml() output (first 600 chars) ==='
$fbXml = $newFb.ProduceXml()
$preview = if ($fbXml.Length -gt 600) { $fbXml.Substring(0, 600) + "`n... [truncated]" } else { $fbXml }
Write-Host $preview
Write-Host '=== end ==='

# Try adding a method to the FB.
Write-Host ''
Write-Host "Adding method SpikeMethod with kind=$MethodKind..."
try {
    $newMethod = $newFb.CreateChild('SpikeMethod', $MethodKind, '', '')
    Write-Host 'CreateChild(method) succeeded.'

    Write-Host ''
    Write-Host '=== Method ProduceXml() output (first 600 chars) ==='
    $mxml = $newMethod.ProduceXml()
    $mpreview = if ($mxml.Length -gt 600) { $mxml.Substring(0, 600) + "`n... [truncated]" } else { $mxml }
    Write-Host $mpreview
    Write-Host '=== end ==='
} catch {
    Write-Host "CreateChild(method) FAILED with kind=$MethodKind : $_" -ForegroundColor Red
    Write-Host 'Try kinds: 603, 608, 609.' -ForegroundColor Yellow
}

if ($CleanUp) {
    Write-Host ''
    Write-Host 'Attempting cleanup: removing FB_SpikeTest...'
    try {
        $parent.DeleteChild('FB_SpikeTest')
        Write-Host 'Deleted.'
    } catch {
        Write-Host "DeleteChild failed: $_" -ForegroundColor Yellow
        Write-Host 'Please remove FB_SpikeTest manually from XAE.'
    }
} else {
    Write-Host ''
    Write-Host 'NOTE: FB_SpikeTest left in project. Delete it manually before continuing.'
}

Write-Host ''
Write-Host 'Spike complete. Update SPIKE_NOTES.md with kind values and GUID format.'
