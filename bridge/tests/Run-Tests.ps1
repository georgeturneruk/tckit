<#
.SYNOPSIS
    Entry point for the PowerShell bridge Pester suite.

.DESCRIPTION
    Runs all *.Tests.ps1 under bridge/tests/. Hardware-free by default.
    Pass -IntegrationTests to include tests tagged 'Integration' (which
    require a live TwinCAT bench machine with XAE attached).

.PARAMETER IntegrationTests
    Include tests tagged 'Integration'. Default: excluded.

.PARAMETER Detailed
    Show per-test output rather than the default summary.

.NOTES
    Requires Pester 5.x. On PowerShell 5.1, install with:
      Install-Module Pester -Force -SkipPublisherCheck -MinimumVersion 5.0
#>
param(
    [switch]$IntegrationTests,
    [switch]$Detailed
)

$ErrorActionPreference = 'Stop'

Import-Module Pester -MinimumVersion 5.0 -ErrorAction Stop

$config = New-PesterConfiguration
$config.Run.Path = $PSScriptRoot
$config.Output.Verbosity = if ($Detailed) { 'Detailed' } else { 'Normal' }
$config.Run.PassThru = $true
if (-not $IntegrationTests) {
    $config.Filter.ExcludeTag = @('Integration')
}

$result = Invoke-Pester -Configuration $config
exit ($result.FailedCount)
