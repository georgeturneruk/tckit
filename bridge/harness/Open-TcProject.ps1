<#
.SYNOPSIS
    Open a TwinCAT solution in XAE.

.DESCRIPTION
    Idempotent: if the requested solution is already loaded in the attached
    DTE instance, returns success without reopening.

.PARAMETER SolutionPath
    Absolute path to the .sln file.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$SolutionPath,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $SolutionPath) { return @{ success = $false; error = 'SolutionPath required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $SolutionPath | Out-Null

    return @{ success = $true; details = @{ solution = $dte.Solution.FullName } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
