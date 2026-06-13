<#
.SYNOPSIS
    Report the solution currently open in the attached TcXaeShell, plus the
    names of the PLC projects it contains.

.DESCRIPTION
    TcKit resolves "which project" from whatever solution is open in the
    attached instance, not from a configured path. This route exposes that
    so the (filesystem-only) reader can learn the project's location on disk:
    attach to the running XAE, read Solution.FullName, and enumerate the PLC
    projects under each TwinCAT project's TIPC node.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.

.OUTPUTS
    @{ success = bool; solution_path = string; plc_projects = @(string...) }
#>
param(
    [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode    = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode

    $solutionPath = ''
    try { $solutionPath = $dte.Solution.FullName } catch { }
    if (-not $solutionPath) {
        return @{
            success = $false
            error   = 'No solution is open in TcXaeShell. Open your project in XAE (or call open_project) first.'
        }
    }

    # Best-effort PLC enumeration: the solution path alone is enough for the
    # reader, so a failure to walk TIPC must not fail the whole lookup.
    $plcProjects = @()
    try {
        $managers = Get-TcSysManagers -Dte $dte
        foreach ($sm in $managers) {
            try {
                $tipc = $sm.LookupTreeItem('TIPC')
                for ($i = 1; $i -le $tipc.ChildCount; $i++) {
                    $plcProjects += $tipc.Child($i).Name
                }
            } catch { continue }
        }
    } catch { }

    return @{
        success       = $true
        solution_path = $solutionPath
        plc_projects  = @($plcProjects)
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
