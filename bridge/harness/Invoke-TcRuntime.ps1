<#
.SYNOPSIS
    Start or restart the TwinCAT runtime on a target via COM.

.DESCRIPTION
    Calls StartRestartTwinCAT() on the system-manager root of the loaded
    TwinCAT project, after pointing it at the requested target NetId.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
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

    $sm = $null
    foreach ($proj in $dte.Solution.Projects) {
        $obj = $null
        try { $obj = $proj.Object } catch { continue }
        if ($null -eq $obj) { continue }
        try { $obj.LookupTreeItem('TIPC') | Out-Null; $sm = $obj; break } catch { continue }
    }
    if ($null -eq $sm) { return @{ success = $false; error = 'No TwinCAT project (ITcSysManager) found.' } }

    try { $sm.SetTargetNetId($TargetAmsId) } catch {
        return @{ success = $false; error = "SetTargetNetId failed: $($_.Exception.Message)" }
    }

    try {
        $sm.StartRestartTwinCAT()
    } catch {
        return @{ success = $false; error = "StartRestartTwinCAT failed: $($_.Exception.Message)" }
    }

    return @{ success = $true; details = @{ target = $TargetAmsId } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
