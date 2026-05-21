<#
.SYNOPSIS
    Delete a Data Unit Type (struct, enum, union, alias) from a PLC project.

.DESCRIPTION
    DUTs live under TIPC^<plc>^<plc> Project^DUTs (see Add-TcDut.ps1).
    Searches the DUTs subtree by name, validates the found item is a
    DUT (so a same-named folder isn't deleted by mistake), then calls
    DeleteChild on the parent. Parent is resolved via PathName so DUTs
    nested in sub-folders are handled.

    ALIAS DUTs are recognised by ItemType 623 even though writer-side
    creation is not yet supported; deletion is safe to expose for
    projects that already contain alias DUTs.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the DUT to delete.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $duts = Get-TcDutsFolder -SysManager $sm -PlcName $plcName
    $item = Find-TcChild -Root $duts -Name $Name
    if ($null -eq $item -or $item.Name -eq $duts.Name) {
        return @{ success = $false; error = "DUT '$Name' not found under DUTs of '$plcName'." }
    }

    # 605 enum, 606 struct, 607 union, 623 alias.
    $dutKinds = @(
        (Get-TcKind -Type 'struct'),
        (Get-TcKind -Type 'enum'),
        (Get-TcKind -Type 'union'),
        623
    )
    # ItemType (not ItemSubType) carries the kind constant on this XAE version.
    $subType = 0
    try { $subType = [int]$item.ItemType } catch { $subType = 0 }
    if ($dutKinds -notcontains $subType) {
        return @{
            success = $false
            error = "'$Name' is not a DUT (kind=$subType). Use the matching delete tool (delete_pou, delete_gvl, delete_folder)."
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
