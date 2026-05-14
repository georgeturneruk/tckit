#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Run a TcUnit test cycle to completion against a target runtime.

.DESCRIPTION
    Per ADR-0006: orchestrates a full TcUnit run end-to-end. Sequence:

      1. Resolve the PLC project name (auto on single-project sln).
      2. Resolve the TcUnit-ResultExportXmlPath from the project's GVL
         declarations (falls back to the canonical default).
      3. Delete the existing XML file so we can detect the new write.
      4. Capture the start epoch.
      5. Ensure the target runtime is in Run mode (Invoke-TcRuntime -Wait).
      6. Open a TcSession on the PLC runtime port (851) and poll
         TcUnit.G_TestRunner.bTestSuitesFinished until true or
         -TimeoutSeconds expires.
      7. Wait for the XML file to land with mtime > start epoch.
      8. Read live summary counters via Read-TcValue and return them
         alongside the XML path (the full structured shape comes from
         /results).

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER TargetAmsId
    AMS Net ID of the target runtime. Falls back to TARGET_AMS_ID env var.

.PARAMETER PlcName
    PLC project hosting the test suites. Optional on single-project slns.

.PARAMETER TimeoutSeconds
    How long to wait for bTestSuitesFinished. Default 120s.

.PARAMETER PollIntervalMs
    Polling cadence for the suites-finished symbol. Default 500ms.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH,
    [string]$TargetAmsId    = $env:TARGET_AMS_ID,
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [int]   $TimeoutSeconds = 120,
    [int]   $PollIntervalMs = 500,
    [string]$ComVersion     = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode        = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '_TcUnit.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    # ----------------------------------------------------------------
    # Attach DTE, resolve PLC project + XML path (compile-time concerns)
    # ----------------------------------------------------------------
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte
    $resolvedPlc = Resolve-TcPlcName -SysManager $sm -Explicit $PlcName
    $plcNode = Get-TcPlcProjectNode -SysManager $sm -PlcName $resolvedPlc
    $xmlPath = Get-TcUnitXmlPath -PlcNode $plcNode

    # ----------------------------------------------------------------
    # Stale-XML mitigation: delete + record start epoch
    # ----------------------------------------------------------------
    if (Test-Path -LiteralPath $xmlPath) {
        try { Remove-Item -LiteralPath $xmlPath -Force } catch {
            return @{ success = $false; error = "Could not delete stale XML at $($xmlPath): $($_.Exception.Message)" }
        }
    }
    $startEpoch = Get-Date
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # ----------------------------------------------------------------
    # Ensure runtime is in Run mode (Invoke-TcRuntime delegates to
    # Restart-TwinCAT via TcXaeMgmt)
    # ----------------------------------------------------------------
    $runtimeScript = Join-Path $PSScriptRoot 'Invoke-TcRuntime.ps1'
    $runtimeResult = & $runtimeScript `
        -TargetAmsId $TargetAmsId `
        -Mode 'Run' `
        -Wait $true `
        -WaitTimeoutSec 30
    if (-not $runtimeResult.success) {
        return @{
            success = $false
            error   = "Runtime did not reach Run mode: $($runtimeResult.error)"
            details = @{ runtime = $runtimeResult }
        }
    }

    # ----------------------------------------------------------------
    # Open a TcSession on the PLC runtime + poll bTestSuitesFinished
    # ----------------------------------------------------------------
    $session = $null
    try {
        $session = New-TcSession -NetId $TargetAmsId -Port 851
        $finished = Wait-TcSymbolEquals `
            -Session $session `
            -Path 'TcUnit.G_TestRunner.bTestSuitesFinished' `
            -Expected $true `
            -TimeoutMs ($TimeoutSeconds * 1000) `
            -PollIntervalMs $PollIntervalMs
        if (-not $finished.success) {
            return @{
                success = $false
                error   = "Tests did not finish within ${TimeoutSeconds}s (bTestSuitesFinished still false)."
                details = @{ xml_path = $xmlPath; elapsed_ms = $finished.elapsed_ms }
            }
        }

        # ------------------------------------------------------------
        # Wait for the XML write to land + read live counters
        # ------------------------------------------------------------
        $fresh = Wait-TcFileFresh -Path $xmlPath -After $startEpoch -TimeoutMs 5000
        if (-not $fresh.success) {
            return @{
                success = $false
                error   = "TcUnit XML at $xmlPath not refreshed within 5s of suites finishing."
                details = @{ xml_path = $xmlPath; suites_finished_ms = $finished.elapsed_ms }
            }
        }

        $summary = @{
            suites           = 0
            tests            = 0
            asserts          = 0
            failures         = 0
            errors           = 0
            duration_seconds = $sw.Elapsed.TotalSeconds
        }
        # Live counter symbols. Best-effort: if a symbol is missing on a
        # particular TcUnit version, leave that field at its default.
        $counterMap = @{
            suites   = 'TcUnit.G_TestRunner.nNumberOfTestSuites'
            tests    = 'TcUnit.G_TestRunner.nNumberOfTestCases'
            asserts  = 'TcUnit.G_TestRunner.nNumberOfAsserts'
            failures = 'TcUnit.G_TestRunner.nNumberOfFailedTests'
            errors   = 'TcUnit.G_TestRunner.nNumberOfTestCaseErrors'
        }
        foreach ($key in $counterMap.Keys) {
            try {
                $summary[$key] = [int](Read-TcValue -Session $session -Path $counterMap[$key])
            } catch {
                # Symbol unknown on this TcUnit version; leave default 0.
            }
        }

        $sw.Stop()
        return @{
            success          = $true
            duration_seconds = $sw.Elapsed.TotalSeconds
            summary          = $summary
            xml_path         = $xmlPath
        }
    }
    finally {
        if ($null -ne $session) {
            try { Close-TcSession -Session $session } catch { }
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
