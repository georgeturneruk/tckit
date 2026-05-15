<#
.SYNOPSIS
    Parse a TcUnit XML results file into the structured TestResults shape.

.DESCRIPTION
    Reads the XML at -XmlPath (defaulting to the xUnit publisher's
    standard output path for a PLC at runtime port 851) and returns a
    hashtable matching the Python TestResults dataclass.

    TcUnit emits JUnit-compatible XML:

      <testsuites tests="N" failures="N" errors="N" time="N">
        <testsuite name="..." tests="N" failures="N" errors="N" time="N">
          <testcase name="..." classname="..." time="N">
            <failure message="..." type="...">expected / actual body</failure>
          </testcase>
        </testsuite>
      </testsuites>

    JUnit has no standard "asserts" or "expected/actual/line" fields, but
    TcUnit's failure messages carry that detail in the body text. The
    parser extracts expected / actual / line on a best-effort basis using
    common TcUnit message patterns; everything else falls back to the
    failure message verbatim.

.PARAMETER ProjectPath
    Currently unused; accepted for symmetry with the other harness scripts
    and so callers can keep posting the same payload shape.

.PARAMETER PlcName
    Currently unused; accepted for symmetry with the other harness scripts.

.PARAMETER TargetAmsId
    Currently unused; accepted for symmetry with the other harness scripts.

.PARAMETER XmlPath
    Override the XML path. Defaults to the xUnit publisher's standard
    output for a PLC at runtime port 851; pass explicitly if the
    project overrides GVL_Param_TcUnit.xUnitFilePath.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$XmlPath     = '',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '_TcUnit.psm1') -Force

function ConvertTo-TcUnitFailure {
    <#
    .SYNOPSIS
        Map a JUnit <failure> element to the AssertFailure shape:
        @{ message; expected; actual; line }.
    #>
    param([Parameter(Mandatory)]$Node)

    $message = ''
    try { $message = [string]$Node.message } catch { }
    if (-not $message) { $message = [string]$Node.InnerText }
    $body = [string]$Node.InnerText

    $expected = ''
    $actual   = ''
    $line     = 0

    # TcUnit / common JUnit forms. Match expected and actual independently
    # so we don't have to model the separator combinatorially.
    #   "Expected: <e>, but was: <a>"
    #   "Expected '<e>' Actual '<a>'"
    #   "Expected = <e>; Actual = <a>"
    $mexp = [regex]::Match($body, '(?i)expected\s*[:=]?\s*[''"]?([^\s,;''"]+)[''"]?')
    if ($mexp.Success) { $expected = $mexp.Groups[1].Value.Trim() }
    $mact = [regex]::Match($body, '(?i)(?:but\s*was|actual)\s*[:=]?\s*[''"]?([^\s,;''"]+)[''"]?')
    if ($mact.Success) { $actual = $mact.Groups[1].Value.Trim() }

    # "line <N>" or "line: <N>" or "(<N>,<col>):"
    $ml = [regex]::Match($body, '(?i)\bline\s*[:=]?\s*(\d+)')
    if ($ml.Success) {
        $line = [int]$ml.Groups[1].Value
    } else {
        $mp = [regex]::Match($body, '\((\d+)(?:,\d+)?\)\s*:')
        if ($mp.Success) { $line = [int]$mp.Groups[1].Value }
    }

    return @{
        message  = $message
        expected = $expected
        actual   = $actual
        line     = $line
    }
}

function ConvertTo-TcUnitTestCase {
    param([Parameter(Mandatory)]$Node)

    $name = ''
    try { $name = [string]$Node.name } catch { }

    $duration = $null
    try { $duration = [double]([string]$Node.time) } catch { }

    $failureNodes = @()
    if ($Node.HasChildNodes) {
        foreach ($child in $Node.ChildNodes) {
            if ($child.LocalName -eq 'failure' -or $child.LocalName -eq 'error') {
                $failureNodes += $child
            }
        }
    }
    $failures = @($failureNodes | ForEach-Object { ConvertTo-TcUnitFailure -Node $_ })

    # TcUnit-specific assert count: try an "asserts" attribute first, then
    # fall back to max(1, failures.Count) so a passing case at least reports 1.
    $asserts = 0
    try {
        if ($Node.HasAttribute('asserts')) {
            $asserts = [int]([string]$Node.GetAttribute('asserts'))
        }
    } catch { }
    if ($asserts -eq 0) { $asserts = [Math]::Max(1, $failures.Count) }

    return @{
        name             = $name
        passed           = ($failures.Count -eq 0)
        asserts          = $asserts
        failures         = $failures
        duration_seconds = $duration
    }
}

function ConvertTo-TcUnitTestSuite {
    param([Parameter(Mandatory)]$Node)

    $name = ''
    try { $name = [string]$Node.name } catch { }

    $tests = @()
    if ($Node.HasChildNodes) {
        foreach ($child in $Node.ChildNodes) {
            if ($child.LocalName -eq 'testcase') {
                $tests += ConvertTo-TcUnitTestCase -Node $child
            }
        }
    }

    return @{
        name  = $name
        tests = $tests
    }
}

function Get-IntAttribute {
    param($Node, [string]$Name, [int]$Default = 0)
    try {
        if ($Node.HasAttribute($Name)) {
            return [int]([string]$Node.GetAttribute($Name))
        }
    } catch { }
    return $Default
}

function Get-DoubleAttribute {
    param($Node, [string]$Name, [double]$Default = 0.0)
    try {
        if ($Node.HasAttribute($Name)) {
            return [double]([string]$Node.GetAttribute($Name))
        }
    } catch { }
    return $Default
}

try {
    # ----------------------------------------------------------------
    # Resolve XML path — default to the xUnit publisher's standard
    # output. Callers that override GVL_Param_TcUnit.xUnitFilePath via
    # library parameters must pass -XmlPath explicitly.
    # ----------------------------------------------------------------
    if (-not $XmlPath) {
        $XmlPath = Get-TcUnitDefaultXmlPath
    }

    if (-not (Test-Path -LiteralPath $XmlPath)) {
        return @{ success = $false; error = "TcUnit results XML not found at $XmlPath."; xml_path = $XmlPath }
    }

    [xml]$doc = Get-Content -LiteralPath $XmlPath -Raw

    $root = $doc.DocumentElement
    if ($null -eq $root) {
        return @{ success = $false; error = "TcUnit results XML at $XmlPath has no root element."; xml_path = $XmlPath }
    }

    # ----------------------------------------------------------------
    # Suites
    # ----------------------------------------------------------------
    $suiteNodes = @()
    if ($root.LocalName -eq 'testsuites') {
        $suiteNodes = @($root.SelectNodes('testsuite'))
    } elseif ($root.LocalName -eq 'testsuite') {
        $suiteNodes = @($root)
    } else {
        return @{ success = $false; error = "Unexpected root element '$($root.LocalName)' in $XmlPath (expected testsuites or testsuite)."; xml_path = $XmlPath }
    }

    $suites = @($suiteNodes | ForEach-Object { ConvertTo-TcUnitTestSuite -Node $_ })

    # ----------------------------------------------------------------
    # Summary
    # ----------------------------------------------------------------
    # Prefer the JUnit root's aggregate attributes; fall back to summing
    # the parsed suites for anything missing.
    $summarySource = if ($root.Name -eq 'testsuites') { $root } else { $null }

    $summary = @{
        suites           = $suites.Count
        tests            = 0
        asserts          = 0
        failures         = 0
        errors           = 0
        duration_seconds = 0.0
    }
    if ($null -ne $summarySource) {
        $summary.tests            = Get-IntAttribute -Node $summarySource -Name 'tests'
        $summary.failures         = Get-IntAttribute -Node $summarySource -Name 'failures'
        $summary.errors           = Get-IntAttribute -Node $summarySource -Name 'errors'
        $summary.duration_seconds = Get-DoubleAttribute -Node $summarySource -Name 'time'
    }
    if ($summary.tests -eq 0) {
        foreach ($s in $suites) { $summary.tests += $s.tests.Count }
    }
    foreach ($s in $suites) {
        foreach ($t in $s.tests) { $summary.asserts += $t.asserts }
    }
    if ($summary.failures -eq 0) {
        foreach ($s in $suites) {
            foreach ($t in $s.tests) { if (-not $t.passed) { $summary.failures += 1 } }
        }
    }

    return @{
        success  = $true
        suites   = $suites
        summary  = $summary
        xml_path = $XmlPath
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
