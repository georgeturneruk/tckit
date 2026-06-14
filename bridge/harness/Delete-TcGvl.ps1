<#
.SYNOPSIS
    Delete a Global Variable List (GVL) from a PLC project.

.DESCRIPTION
    GVLs are tree items (kind 615) created under the same POUs folder
    as POUs themselves; see Add-TcGvl.ps1. This script searches the
    POUs subtree by name, validates the found item is a GVL (so a
    same-named POU or folder isn't deleted by mistake), then calls
    DeleteChild on the GVL's parent. The parent is resolved via the
    item's PathName so GVLs nested in folders are handled.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the GVL to delete.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$Name,
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

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    $item = Find-TcChild -Root $pous -Name $Name
    if ($null -eq $item -or $item.Name -eq $pous.Name) {
        return @{ success = $false; error = "GVL '$Name' not found under POUs of '$plcName'." }
    }

    $gvlKind = Get-TcKind -Type 'gvl'
    # ItemType (not ItemSubType) carries the kind constant on this XAE version.
    $subType = 0
    try { $subType = [int]$item.ItemType } catch { $subType = 0 }
    if ($subType -ne $gvlKind) {
        return @{
            success = $false
            error = "'$Name' is not a GVL (kind=$subType, expected $gvlKind). Use the matching delete tool (delete_pou, delete_folder, delete_dut)."
        }
    }

    $parentPath = Remove-TcTreeItem -SysManager $sm -Item $item
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; plc = $plcName; parent_path = $parentPath; kind = $subType }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
