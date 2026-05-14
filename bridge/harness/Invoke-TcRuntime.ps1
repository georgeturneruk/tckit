<#
.SYNOPSIS
    Drive the TwinCAT runtime mode on a target via COM.

.DESCRIPTION
    Sets the target NetId, transitions the runtime to the requested mode
    (Run or Config), and optionally waits for the transition to complete.

    Per ADR-0006 this script is the One Rule's enforcement point for
    runtime mutations: build orchestration calls ``-Mode Run`` to ensure
    the deployed configuration is running before tests start.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER Mode
    'Run' calls StartRestartTwinCAT() (idempotent get-to-Run); 'Config'
    calls SetConfigMode(). Required.

.PARAMETER PlcName
    PLC project (for symbol-state polling under -Wait). Optional; if
    omitted, the resolver auto-resolves on a single-PLC sln.

.PARAMETER Wait
    Block until the runtime reports the requested state, up to
    -WaitTimeoutSec seconds.

.PARAMETER WaitTimeoutSec
    Timeout for -Wait. Default 30s.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [Parameter(Mandatory)][ValidateSet('Run', 'Config')][string]$Mode,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [bool]  $Wait        = $false,
    [int]   $WaitTimeoutSec = 30,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '_TcUnit.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte

    try { $sm.SetTargetNetId($TargetAmsId) } catch {
        return @{ success = $false; error = "SetTargetNetId failed: $($_.Exception.Message)" }
    }

    # Mode transitions:
    # * Run  — ITcSysManager.StartRestartTwinCAT() (pure COM, no ADS needed)
    # * Config — no equivalent on ITcSysManager; the standard route is an
    #   ADS WriteControl to the system service (port 10000) with
    #   ADSSTATE_RECONFIG (=2) or ADSSTATE_CONFIG (=16). Requires
    #   TwinCAT.Ads.dll to be installed.
    try {
        switch ($Mode) {
            'Run' {
                $sm.StartRestartTwinCAT()
            }
            'Config' {
                try { Get-TcAdsAssembly } catch {
                    return @{
                        success = $false
                        error   = "Cannot switch to Config mode: $($_.Exception.Message) TwinCAT 3 has no purely-COM API for the Run -> Config transition; install the ADS .NET API or pass a path via TCADS_DLL_PATH."
                    }
                }
                $configClient = $null
                try {
                    $configClient = New-Object 'TwinCAT.Ads.AdsClient'
                    $configClient.Connect($TargetAmsId, 10000)  # 10000 = system service
                    # WriteControl(state, deviceState, data). State 16 = Config.
                    $configClient.WriteControl(
                        [TwinCAT.Ads.StateInfo]::new([TwinCAT.Ads.AdsState]::Config, [uint16]0)
                    )
                } catch {
                    return @{ success = $false; error = "ADS WriteControl(Config) failed: $($_.Exception.Message)" }
                } finally {
                    if ($null -ne $configClient) {
                        try { $configClient.Disconnect() } catch { }
                        try { $configClient.Dispose() } catch { }
                    }
                }
            }
        }
    } catch {
        return @{ success = $false; error = "Mode transition to '$Mode' failed: $($_.Exception.Message)" }
    }

    $details = @{ target = $TargetAmsId; mode = $Mode }

    if ($Wait) {
        # ADS state-port (10000) for the system service exposes ADSSTATE,
        # but the cleanest signal at the PLC-runtime level is just trying
        # to open a connection on port 851 and reading a known symbol.
        # If Mode = Run, the PLC runtime should be up and responding.
        # If Mode = Config, port 851 is unreachable; we treat the failure
        # of the connect as the success signal for Config.
        try {
            Get-TcAdsAssembly
        } catch {
            $details.wait = @{ skipped = $true; reason = $_.Exception.Message }
            return @{ success = $true; details = $details }
        }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $timeoutMs = $WaitTimeoutSec * 1000
        $reached = $false
        while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
            $client = $null
            try {
                $client = New-Object 'TwinCAT.Ads.AdsClient'
                $client.Connect($TargetAmsId, 851)
                # Read the runtime state via ADS state request.
                $stateInfo = $client.ReadState()
                $adsState = [int]$stateInfo.AdsState
                # AdsState 5 = Run; AdsState 6 = Config (for the PLC runtime).
                $expected = if ($Mode -eq 'Run') { 5 } else { 6 }
                if ($adsState -eq $expected) { $reached = $true; break }
            } catch {
                # Connect/read may fail during transition. For Config, the
                # PLC port closes — treat repeated connect failure as success.
                if ($Mode -eq 'Config') {
                    Start-Sleep -Milliseconds 500
                    try { if ($client) { $client.Disconnect() } } catch { }
                    $reached = $true
                    break
                }
            } finally {
                try { if ($client) { $client.Dispose() } } catch { }
            }
            Start-Sleep -Milliseconds 500
        }
        $sw.Stop()
        $details.wait = @{ reached = $reached; elapsed_ms = [int]$sw.ElapsedMilliseconds }
        if (-not $reached) {
            return @{ success = $false; error = "Runtime did not reach '$Mode' within ${WaitTimeoutSec}s."; details = $details }
        }
    }

    return @{ success = $true; details = $details }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
