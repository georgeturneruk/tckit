#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Drive the TwinCAT runtime mode on a target via the TcXaeMgmt module.

.DESCRIPTION
    Thin wrapper around TcXaeMgmt's Restart-TwinCAT cmdlet. The bench
    confirmed there's no purely-COM API for the Run -> Config transition
    in TC3 (StartRestartTwinCAT only goes to Run; SetConfigMode does not
    exist on ITcSysManager). Beckhoff's own PowerShell module already
    wraps the ADS WriteControl correctly with state polling and signed
    code, so we lean on it rather than rolling our own ADS client.

    See
    https://infosys.beckhoff.com/content/1033/tc3_ads_ps_tcxaemgmt/15420058507.html
    for Restart-TwinCAT documentation.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER Mode
    'Run'    -> Restart-TwinCAT -Command Restart (system into Run mode)
    'Config' -> Restart-TwinCAT -Command Config  (system into Config mode)

.PARAMETER Wait
    Block until the runtime reports the requested state, up to
    -WaitTimeoutSec seconds. When $false, returns immediately after the
    WriteControl request (Restart-TwinCAT's -NoWait).

.PARAMETER WaitTimeoutSec
    Timeout for -Wait. Default 45s (matches Restart-TwinCAT's default).

.PARAMETER ProjectPath
    Currently unused for the mode transition (TcXaeMgmt operates on the
    target directly via ADS). Accepted for backward compatibility with
    callers that still pass it; ignored.
#>
param(
    [string]$TargetAmsId    = $env:TARGET_AMS_ID,
    [Parameter(Mandatory)][ValidateSet('Run', 'Config')][string]$Mode,
    [bool]  $Wait           = $false,
    [int]   $WaitTimeoutSec = 45,
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

try {
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    $cmdMap = @{
        'Run'    = 'Restart'  # boot into Run mode
        'Config' = 'Config'   # boot into Config mode
    }
    $restartCommand = $cmdMap[$Mode]

    $args = @{
        NetId       = $TargetAmsId
        Command     = $restartCommand
        Force       = $true
        WaitTimeout = $WaitTimeoutSec * 1000
        ThrowError  = $true
    }
    if (-not $Wait) { $args.NoWait = $true }

    Restart-TwinCAT @args | Out-Null

    return @{
        success = $true
        details = @{
            target  = $TargetAmsId
            mode    = $Mode
            command = $restartCommand
            waited  = [bool]$Wait
        }
    }
}
catch {
    return @{
        success = $false
        error   = "Restart-TwinCAT -Command $($cmdMap[$Mode]) on $TargetAmsId failed: $($_.Exception.Message)"
    }
}
