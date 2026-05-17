<#
.SYNOPSIS
    Diagnostic: enumerate the candidate TcUnit XML paths the bridge
    would consider, with existence flags. Used by tckit doctor's
    TcUnit results section. See ADR-0011.

.DESCRIPTION
    Wraps Resolve-TcUnitXmlCandidates from _TcUnit.psm1 so the
    bridge can expose the same fallback ladder via HTTP without
    forcing the caller to run a TcUnit cycle. Read-only; no DTE
    attach, no runtime contact.

.PARAMETER Port
    PLC runtime port. Default 851 (the TcUnit standard).
#>
param(
    [int]$Port = 851
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcUnit.psm1') -Force

try {
    $candidates = Resolve-TcUnitXmlCandidates -Port $Port
    return @{
        success         = $true
        env_override    = $candidates.env_override
        env_exists      = $candidates.env_exists
        kernel_path     = $candidates.kernel_path
        kernel_exists   = $candidates.kernel_exists
        umrt_candidates = $candidates.umrt_candidates
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
