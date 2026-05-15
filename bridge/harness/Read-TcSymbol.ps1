#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Read PLC symbols by instance path on a running runtime via ADS.

.DESCRIPTION
    Best-effort reader: opens a TcXaeMgmt session on the standard PLC
    runtime port (851), iterates the requested paths, and returns each
    value as a string under details.values. Paths that can't be
    resolved land in details.errors but do not fail the call — same
    convention as the ReadSymbols convenience parameter on /tcunit-run.

    The runtime must already be in Run mode; use /deploy + /runtime
    first if needed. Symbol IO requires a running PLC.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER Paths
    Newline-separated list of symbol instance paths (e.g.
    "MAIN.suite.Tests[1].TestIsFailed"). Newline-separated rather
    than a JSON array because the bridge's request decoder collapses
    nested string arrays unhelpfully on PowerShell 5.1.

.PARAMETER Port
    PLC runtime port. Default 851 (the standard first-PLC port).
#>
param(
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$Paths       = '',
    [int]   $Port        = 851
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

try {
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }
    if (-not $Paths)       {
        return @{
            success = $true
            values  = @{}
            errors  = @{}
            port    = $Port
        }
    }

    $values = @{}
    $errors = @{}
    $session = $null
    try {
        $session = New-TcSession -NetId $TargetAmsId -Port $Port
        foreach ($raw in ($Paths -split "`n")) {
            $path = $raw.Trim()
            if (-not $path) { continue }
            try {
                $values[$path] = [string](Read-TcValue -Session $session -Path $path)
            } catch {
                $errors[$path] = $_.Exception.Message
            }
        }
    }
    finally {
        if ($null -ne $session) {
            try { Close-TcSession -Session $session } catch { }
        }
    }

    return @{
        success = $true
        values  = $values
        errors  = $errors
        port    = $Port
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
