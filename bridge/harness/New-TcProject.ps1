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
    Name of the PLC sub-project. Defaults to "${Name}_Plc" so the PLC's
    name does not collide with the sln / TwinCAT-project name. TwinCAT
    treats the VS Project node (wrapping the .tspproj) and the PLC
    project under TIPC as separate tree items; giving them the same
    name has caused TcXaeShell to crash on solution load. Pass
    explicitly if you need the legacy "PLC == sln" naming.

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
    if (-not $PlcName) { $PlcName = "${Name}_Plc" }

    if (-not $TemplatePath) {
        $TemplatePath = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PlcTemplate\TwinCAT PLC Project.tspproj'
    }
    if (-not (Test-Path $TemplatePath)) {
        return @{ success = $false; error = "Template not found: $TemplatePath. Pass -TemplatePath." }
    }
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode

    # Step 1: empty solution shell. COM methods on Solution emit objects
    # into the PowerShell output stream; suppress so the trailing hashtable
    # is the only value the harness returns.
    #
    # On a fresh XAE attach the Solution object is in an "uninitialised"
    # state where method calls fail with "null-valued expression". On a
    # pre-loaded XAE Solution.Create throws because something's already
    # there. We try Create directly first; if it fails, close any
    # loaded sln and retry once.
    try {
        $dte.Solution.Create($Path, $Name) | Out-Null
    } catch {
        try { $dte.Solution.Close($false) | Out-Null } catch { }
        $dte.Solution.Create($Path, $Name) | Out-Null
    }

    # Step 2: TwinCAT project from template.
    $dte.Solution.AddFromTemplate($TemplatePath, $Path, $Name, $false) | Out-Null

    # Step 3: PLC sub-project under TIPC.
    $sm = Get-TcSysManager -Dte $dte
    $tipc = $sm.LookupTreeItem('TIPC')
    $tipc.CreateChild($PlcName, 0, $null, 'Standard PLC Template.plcproj') | Out-Null

    # Step 4: persist the solution.
    $solutionPath = Join-Path $Path ("$Name.sln")
    $dte.Solution.SaveAs($solutionPath) | Out-Null

    return @{
        success = $true
        details = @{ solution_path = $solutionPath; plc = $PlcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
