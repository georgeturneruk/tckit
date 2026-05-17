#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Run a TcUnit test cycle to completion against a target runtime.

.DESCRIPTION
    Per ADR-0006: orchestrates a full TcUnit run end-to-end. Sequence:

      1. Resolve the xUnit publisher's default output path
         (C:\TwinCAT\3.1\Boot\Plc\Port_851\tcunit_xunit_testresults.xml).
         Callers that override GVL_Param_TcUnit.xUnitFilePath via library
         parameters must pass the resolved path back through /results
         explicitly — the bridge does not introspect it off the runtime.
      2. Delete the existing XML file so we can detect the new write.
      3. Capture the start epoch.
      4. Ensure the target runtime is in Run mode (Invoke-TcRuntime -Wait).
      5. Open a TcSession on the PLC runtime port (851) and poll
         GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished until true or
         -TimeoutSeconds expires. (The runner FB lives at
         `GVL_TcUnit.TcUnitRunner`; the "finished" flag is named
         `AllTestSuitesFinished` in current TcUnit releases — see
         tcunit/TcUnit FB_TcUnitRunner.TcPOU. The ADS symbol tree
         does NOT prefix library symbols with the placeholder name
         (no `TcUnit.` prefix), even though source code references
         `TcUnit.GVL_TcUnit`.)
      6. Wait for the XML file to land with mtime > start epoch.
      7. Read live summary counters via Read-TcValue and return them
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
    # Newline-separated list of symbol instance paths to read once
    # AllTestSuitesFinished flips. Newline-separated rather than a
    # `[string[]]` because the bridge's ConvertTo-HashtableDeep collapses
    # nested string arrays in unhelpful ways on PowerShell 5.1. The
    # parameter is also deliberately NOT named `Probes` — PowerShell's
    # advanced-function machinery interferes with that specific name and
    # the splatted value arrives as an empty Hashtable instead of the
    # caller's string.
    [string]$ReadSymbols    = '',
    # When $true (default), inline the parsed test results (summary +
    # failures-only suites + flat failures list) into the response so
    # the model sees pass/fail on the first cycle without a follow-up
    # /results call. Passing tests are omitted to keep payload bounded
    # on large green suites; /results still returns the full per-test
    # list including passes. See ADR-0011.
    [bool]  $IncludeResults = $true,
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
    # Resolve the xUnit publisher's default output path. No DTE attach
    # needed — the path is a known function of the PLC runtime port.
    # ----------------------------------------------------------------
    $xmlPath        = Get-TcUnitDefaultXmlPath
    $resolveWarning = Get-TcUnitXmlResolveWarning

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
            -Path 'GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished' `
            -Expected $true `
            -TimeoutMs ($TimeoutSeconds * 1000) `
            -PollIntervalMs $PollIntervalMs
        if (-not $finished.success) {
            return @{
                success = $false
                error   = "Tests did not finish within ${TimeoutSeconds}s (AllTestSuitesFinished still false)."
                details = @{ xml_path = $xmlPath; elapsed_ms = $finished.elapsed_ms }
            }
        }

        # ------------------------------------------------------------
        # Wait for the XML write to land + read live counters. The XML
        # publisher only writes when `GVL_Param_TcUnit.xUnitEnablePublish`
        # is overridden to TRUE on the consumer PLC (defaults to FALSE).
        # Without that override, the suite finishes and the runner state
        # machine completes correctly but no XML is ever emitted. Tolerate
        # that: report success with `xml_published=false` so /results can
        # tell the caller why there's nothing to parse. Callers needing
        # the XML for CI must enable the publisher.
        # ------------------------------------------------------------
        $fresh = Wait-TcFileFresh -Path $xmlPath -After $startEpoch -TimeoutMs 5000
        $xmlPublished = $fresh.success

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
        # TcUnit exposes the suite count as a global on GVL_TcUnit, but
        # tests / asserts / failures / errors are tracked per-suite, not
        # aggregated as globals — those land via Get-TcUnitResults reading
        # the xUnit XML the publisher writes on completion.
        $counterMap = @{
            suites = 'GVL_TcUnit.NumberOfInitializedTestSuites'
        }
        foreach ($key in $counterMap.Keys) {
            try {
                $summary[$key] = [int](Read-TcValue -Session $session -Path $counterMap[$key])
            } catch {
                # Symbol unknown on this TcUnit version; leave default 0.
            }
        }

        # Optional ad-hoc probes: caller passes a newline-separated list
        # of symbol instance paths and the response includes their current
        # values. Lets the bench smoke read pass/fail (e.g.
        # MAIN.suite.Tests[1].TestIsFailed) without needing the xUnit XML
        # publisher enabled. Best-effort: unreadable symbols land in
        # `probes_errors` rather than failing the run.
        $probes = @{}
        $probesErrors = @{}
        if ($ReadSymbols) {
            foreach ($raw in ($ReadSymbols -split "`n")) {
                $path = $raw.Trim()
                if (-not $path) { continue }
                try {
                    $probes[$path] = [string](Read-TcValue -Session $session -Path $path)
                } catch {
                    $probesErrors[$path] = $_.Exception.Message
                }
            }
        }

        $sw.Stop()

        # Inline parsed results when the XML actually landed: failures-only
        # suite shape plus a flat `failures` list. Lets the model see
        # pass/fail on the first run_tests call without a follow-up /results
        # round trip. Best-effort: any parse error is reported via
        # results_error rather than failing the whole run, so probes /
        # summary still come back.
        $resultsSummary  = $summary
        $resultsSuites   = @()
        $resultsFailures = @()
        $resultsError    = $null
        $resultsIncluded = $false
        if ($IncludeResults -and $xmlPublished) {
            try {
                $parsed = ConvertFrom-TcUnitXml -XmlPath $xmlPath -FailuresOnly $true
                if ($parsed.success) {
                    $resultsSummary  = $parsed.summary
                    $resultsSuites   = $parsed.suites
                    $resultsFailures = $parsed.failures
                    $resultsIncluded = $true
                } else {
                    $resultsError = [string]$parsed.error
                }
            } catch {
                $resultsError = $_.Exception.Message
            }
        }

        $response = @{
            success          = $true
            duration_seconds = $sw.Elapsed.TotalSeconds
            summary          = $resultsSummary
            xml_path         = $xmlPath
            xml_published    = $xmlPublished
            probes           = $probes
            probes_errors    = $probesErrors
            resolve_warning  = $resolveWarning
            results_included = $resultsIncluded
            suites           = $resultsSuites
            failures         = $resultsFailures
        }
        if ($resultsError) { $response.results_error = $resultsError }
        return $response
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
