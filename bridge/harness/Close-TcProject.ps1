<#
.SYNOPSIS
    Close the currently loaded TwinCAT solution in XAE without saving.

.DESCRIPTION
    Calls $dte.Solution.Close($false). Idempotent: if no solution is
    loaded the call returns success.

    Used by callers that need to mutate the project's on-disk XML
    (.plcproj / .TcPOU) without XAE's "project modified outside the
    environment" reload prompt firing. The expected pattern is:

        POST /open    -> attach a fresh DTE to the solution
        POST /close   -> release the in-memory project model
        ... mutate files on disk ...
        POST /open    -> re-attach; XAE reads from the updated files

    This mirrors the close/edit/reopen flow Add-TcLibraryPlaceholder
    uses for library parameter overrides.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode

    $closed = $false
    $previous = $null
    try {
        $previous = $dte.Solution.FullName
    } catch {
        $previous = $null
    }

    if ($previous) {
        $dte.Solution.Close($false) | Out-Null
        $closed = $true
    }

    return @{
        success = $true
        details = @{
            closed             = $closed
            previous_solution  = $previous
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
