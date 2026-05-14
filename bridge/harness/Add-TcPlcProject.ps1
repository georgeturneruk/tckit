<#
.SYNOPSIS
    Add a PLC sub-project to an already-existing TwinCAT solution.

.DESCRIPTION
    Sibling of New-TcProject.ps1, but for the multi-PLC case: an .sln + first
    .plcproj already exist; we add a second (or further) .plcproj under the
    same TIPC node.

    Steps:
      1. Open the solution (idempotent via Open-TcSolution).
      2. Locate the ITcSysManager and its TIPC tree item.
      3. tipc.CreateChild(<plcName>, 0, $null, 'Standard PLC Template.plcproj')
      4. Solution.Save() so the .sln file records the new project.

    See ADR-0009.

.PARAMETER ProjectPath
    Absolute path to the existing .sln file.

.PARAMETER PlcName
    Name of the new PLC sub-project. Must not collide with an existing
    PLC project name in the same sln.

.PARAMETER ProjectType
    Reserved. v1 only accepts 'standard'; passing 'library' returns an
    explicit error. Default 'standard'.
#>
param(
    [string]$ProjectPath,
    [string]$PlcName,
    [string]$ProjectType  = 'standard',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PlcName)     { return @{ success = $false; error = 'PlcName required.' } }
    if ($ProjectType -ne 'standard') {
        return @{ success = $false; error = "ProjectType '$ProjectType' not supported (only 'standard')." }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte
    $tipc = $sm.LookupTreeItem('TIPC')

    # Guard against name collision before CreateChild — clearer error.
    for ($i = 1; $i -le $tipc.ChildCount; $i++) {
        if ($tipc.Child($i).Name -eq $PlcName) {
            return @{ success = $false; error = "PLC project '$PlcName' already exists in solution." }
        }
    }

    $tipc.CreateChild($PlcName, 0, $null, 'Standard PLC Template.plcproj') | Out-Null
    # Persist back to the existing sln file (SaveAs is for fresh sln; Save is
    # the right call when the sln already lives at $ProjectPath). Suppress
    # the COM return value so it doesn't leak into the harness output stream.
    $dte.Solution.SaveAs($dte.Solution.FullName) | Out-Null

    return @{
        success = $true
        details = @{
            solution_path = $ProjectPath
            plc           = $PlcName
            project_type  = $ProjectType
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
