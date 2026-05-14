<#
.SYNOPSIS
    Deploy a built TwinCAT configuration to a target runtime via COM.

.DESCRIPTION
    Sets the target NetId on the chosen TwinCAT project's system manager
    and calls ActivateConfiguration(). In a multi-tsproj sln (one .tsproj
    per PLC, as produced by Add-TcPlcProject) you must pass -PlcName so
    the right TwinCAT project is targeted; in a single-tsproj sln the
    PlcName is optional.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER TargetAmsId
    AMS Net ID of the target (e.g. 192.168.1.100.1.1). Falls back to TARGET_AMS_ID env var.

.PARAMETER PlcName
    Name of the PLC whose containing TwinCAT project should be deployed.
    Optional; falls back to PLC_PROJECT_NAME env var, then to the only
    PLC in the solution if there's exactly one.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null

    $resolvedPlc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $resolvedPlc

    # Set target NetId, then activate.
    try {
        $sm.SetTargetNetId($TargetAmsId)
    } catch {
        return @{ success = $false; error = "SetTargetNetId('$TargetAmsId') failed: $($_.Exception.Message)" }
    }

    try {
        $sm.ActivateConfiguration()
    } catch {
        return @{ success = $false; error = "ActivateConfiguration failed: $($_.Exception.Message)" }
    }

    return @{ success = $true; details = @{ target = $TargetAmsId; plc = $resolvedPlc } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
