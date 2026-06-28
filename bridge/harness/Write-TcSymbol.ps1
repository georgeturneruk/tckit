#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Write PLC symbols by instance path on a running runtime via ADS.

.DESCRIPTION
    Best-effort writer: opens a TcXaeMgmt session on the standard PLC
    runtime port (851), iterates the requested writes, and returns each
    written path under details.written. Paths that fail land in
    details.errors but do not abort remaining writes — same convention
    as Read-TcSymbol.ps1.

    TcXaeMgmt resolves the PLC type from ADS at write time and coerces
    the supplied value. Primitives (int, float, bool, string) work
    without extra annotation. Arrays are passed as PowerShell arrays
    after JSON decode. Structs are passed as PSCustomObjects; if
    Write-TcValue rejects a PSCustomObject, the error is reported
    per-path in details.errors.

    The runtime must already be in Run mode; use /deploy + /runtime
    first if needed.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER WritesJson
    JSON object string mapping symbol instance paths to values.
    Double-encoded to preserve mixed types through PS 5.1 JSON decode
    (e.g. '{"MAIN.nCounter": 42, "GVL.bEnable": true}').

.PARAMETER Port
    PLC runtime port. Default 851 (the standard first-PLC port).
#>
param(
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$WritesJson  = '{}',
    [int]   $Port        = 851
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

try {
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    $writes  = $WritesJson | ConvertFrom-Json
    $entries = @($writes.PSObject.Properties)

    if ($entries.Count -eq 0) {
        return @{
            success = $true
            written = @{}
            errors  = @{}
            port    = $Port
        }
    }

    $written = @{}
    $errors  = @{}
    $session = $null
    try {
        $session = New-TcSession -NetId $TargetAmsId -Port $Port
        foreach ($prop in $entries) {
            $path  = $prop.Name
            $value = $prop.Value
            try {
                Write-TcValue -Session $session -Path $path -Value $value
                $written[$path] = [string]$value
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
        success = ($errors.Count -eq 0)
        written = $written
        errors  = $errors
        port    = $Port
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
