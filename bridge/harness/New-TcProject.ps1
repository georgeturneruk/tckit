<#
.SYNOPSIS
    Create a new TwinCAT solution + PLC project from the standard template.

.DESCRIPTION
    Validated 4-step recipe (see scripts/SPIKE_NOTES.md):

      1. Solution.Create(<dir>, <name>)
      2. Solution.AddFromTemplate(<TwinCAT PLC Project.tspproj>, <dir>, <name>, $false)
      3. tipc.CreateChild(<plcName>, 0, $null, 'Standard PLC Template.plcproj')
      4. Solution.SaveAs(<dir>\<name>.sln)

    GetProjectTemplate() is not available on TcXaeShell Express, so we pass
    the template path directly. PLC sub-project kind is 0 (with template name
    in the 4th arg).

.PARAMETER Name
    Solution name (also used for the .sln file).

.PARAMETER Path
    Directory in which to create the project.

.PARAMETER PlcName
    Name of the PLC sub-project. Defaults to $Name.

.PARAMETER TemplatePath
    Optional explicit template .tspproj path. If omitted, the standard 4026
    install location is used.
#>
param(
    [string]$Name,
    [string]$Path,
    [string]$PlcName      = '',
    [string]$TemplatePath = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name) { return @{ success = $false; error = 'Name required.' } }
    if (-not $Path) { return @{ success = $false; error = 'Path required.' } }
    if (-not $PlcName) { $PlcName = $Name }

    if (-not $TemplatePath) {
        $TemplatePath = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PlcTemplate\TwinCAT PLC Project.tspproj'
    }
    if (-not (Test-Path $TemplatePath)) {
        return @{ success = $false; error = "Template not found: $TemplatePath. Pass -TemplatePath." }
    }
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode

    # Step 1: empty solution shell.
    $dte.Solution.Create($Path, $Name)

    # Step 2: TwinCAT project from template.
    $dte.Solution.AddFromTemplate($TemplatePath, $Path, $Name, $false)

    # Step 3: PLC sub-project under TIPC.
    $sm = Get-TcSysManager -Dte $dte
    $tipc = $sm.LookupTreeItem('TIPC')
    $tipc.CreateChild($PlcName, 0, $null, 'Standard PLC Template.plcproj') | Out-Null

    # Step 4: persist the solution.
    $solutionPath = Join-Path $Path ("$Name.sln")
    $dte.Solution.SaveAs($solutionPath)

    return @{
        success = $true
        details = @{ solution_path = $solutionPath; plc = $PlcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
