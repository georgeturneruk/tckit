<#
.SYNOPSIS
    Delete a folder from a PLC project's source tree.

.DESCRIPTION
    Searches the PLC project's IDE-level subtree (TIPC^<plc>^<plc>
    Project) for a folder with the given name. Validates ItemType=601
    so a same-named POU/GVL/DUT isn't deleted by mistake. Refuses by
    default when the folder is non-empty; pass -Recursive to allow
    cascade. Whether DeleteChild on a non-empty folder cascades is
    undocumented on infosys, so we walk children first for safety when
    recursive deletion is requested.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls
    back to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the folder to delete. May be a leaf folder name or, when
    -ParentPath is supplied, a leaf under that explicit parent.

.PARAMETER ParentPath
    Optional explicit parent path to disambiguate a name that exists in
    multiple subtrees. Format: ``POUs`` or ``POUs/Drives`` or
    ``DUTs/Motors`` (forward-slash separator under the PLC project's
    IDE-level node).

.PARAMETER Recursive
    Allow deleting a folder that still contains children.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$ParentPath  = '',
    [bool]$Recursive     = $false,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName

    $folderKind = Get-TcKind -Type 'folder'
    $folder = $null

    if ($ParentPath) {
        $parent = $plcProj
        foreach ($seg in ($ParentPath -split '[/\\]')) {
            if (-not $seg) { continue }
            $parent = Find-TcChild -Root $parent -Name $seg
            if ($null -eq $parent) {
                return @{ success = $false; error = "Parent path segment '$seg' not found under PLC project '$plcName'." }
            }
        }
        # Direct-child lookup so we don't accidentally descend into matching
        # grandchildren when ParentPath was explicit.
        for ($i = 1; $i -le $parent.ChildCount; $i++) {
            $child = $parent.Child($i)
            if ($child.Name -eq $Name) { $folder = $child; break }
        }
    } else {
        $folder = Find-TcChild -Root $plcProj -Name $Name
        if ($folder -eq $plcProj) { $folder = $null }
    }

    if ($null -eq $folder) {
        return @{ success = $false; error = "Folder '$Name' not found under PLC project '$plcName'." }
    }

    # ItemType (not ItemSubType) carries the kind constant on this XAE version.
    $subType = 0
    try { $subType = [int]$folder.ItemType } catch { $subType = 0 }
    if ($subType -ne $folderKind) {
        return @{
            success = $false
            error = "'$Name' is not a folder (kind=$subType, expected $folderKind). Use the matching delete tool (delete_pou, delete_gvl, delete_dut)."
        }
    }

    $childCount = 0
    try { $childCount = [int]$folder.ChildCount } catch { $childCount = 0 }
    if ($childCount -gt 0 -and -not $Recursive) {
        return @{
            success = $false
            error = "Folder '$Name' is not empty (contains $childCount item(s)); pass recursive=true to cascade."
        }
    }

    # When recursive, drain children first. DeleteChild on a child returned
    # by Child(1) keeps shifting indices, so we always take the first child
    # until empty rather than enumerating by snapshot.
    while ($folder.ChildCount -gt 0) {
        $head = $folder.Child(1)
        $headName = $head.Name
        $folder.DeleteChild($headName)
    }

    $parentPathReported = Remove-TcTreeItem -SysManager $sm -Item $folder
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; plc = $plcName; parent_path = $parentPathReported; recursive = [bool]$Recursive }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
