<#
.SYNOPSIS
    TcUnit-specific helpers: a session-based symbol-equality poll, the xUnit
    publisher's default output path, and file-freshness polling.

.DESCRIPTION
    Sibling module to _TcDte.psm1. _TcDte owns DTE / project-tree navigation;
    this module owns concerns that are specific to the TcUnit runner.

    Runtime ADS work (symbol reads, runtime-state transitions) goes through
    Beckhoff's official TcXaeMgmt module. Callers create a TcSession via
    New-TcSession from TcXaeMgmt and pass it to Wait-TcSymbolEquals here.

    Exported functions:
      Wait-TcSymbolEquals          - poll a PLC symbol on a TcSession until
                                      it equals a value or times out
      Get-TcUnitDefaultXmlPath     - return the xUnit publisher's default
                                      output path for a given PLC port
      Get-TcUnitXmlResolveWarning  - last ambiguity warning recorded by
                                      Get-TcUnitDefaultXmlPath, or empty
      Resolve-TcUnitXmlCandidates  - enumerate all candidate XML paths
                                      (env override, kernel-RT, UmRT glob)
      ConvertFrom-TcUnitXml        - parse TcUnit JUnit XML into the
                                      structured suites/summary hashtable;
                                      optional FailuresOnly mode
      Wait-TcFileFresh             - wait for a file to appear with mtime >
                                      a given epoch
#>

Set-StrictMode -Version Latest

# ------------------------------------------------------------------
# Defaults
# ------------------------------------------------------------------

# TcUnit's xUnit publisher writes to %TC_BOOTPRJPATH%<file>; for a PLC at the
# standard runtime port that resolves to C:\TwinCAT\3.1\Boot\Plc\Port_<port>\.
# The publisher's filename default lives on GVL_Param_TcUnit.xUnitFilePath
# (see https://github.com/tcunit/TcUnit). The previous convention of
# greping a project-defined `TcUnit_ResultExportXmlPath` GVL constant
# was a TcKit-side fiction — TcUnit never read that name. See ADR-0010.
$script:TcUnitDefaultXmlFileName = 'tcunit_xunit_testresults.xml'
$script:TcUnitDefaultPlcPort     = 851

# ------------------------------------------------------------------
# Symbol polling
# ------------------------------------------------------------------

function Wait-TcSymbolEquals {
    <#
    .SYNOPSIS
        Poll a PLC symbol on an existing TcXaeMgmt session until it equals
        the expected value or the timeout expires.

    .PARAMETER Session
        A TcSession created by New-TcSession (from TcXaeMgmt).

    .PARAMETER Path
        Symbol path, e.g. ``TcUnit.G_TestRunner.bTestSuitesFinished``.

    .PARAMETER Expected
        Value the symbol must reach for success.

    .PARAMETER TimeoutMs
        Maximum total wait, in milliseconds.

    .PARAMETER PollIntervalMs
        Sleep between reads, in milliseconds.

    .OUTPUTS
        @{ success = bool; value = <last read>; elapsed_ms = int }
    #>
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Expected,
        [int]$TimeoutMs = 120000,
        [int]$PollIntervalMs = 500
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $last = Read-TcValue -Session $Session -Path $Path
            if ($last -eq $Expected) {
                $sw.Stop()
                return @{ success = $true; value = $last; elapsed_ms = [int]$sw.ElapsedMilliseconds }
            }
        } catch {
            # Symbol may not yet be loaded; tolerate during the early window.
        }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    $sw.Stop()
    return @{ success = $false; value = $last; elapsed_ms = [int]$sw.ElapsedMilliseconds }
}

# ------------------------------------------------------------------
# xUnit publisher default output path
# ------------------------------------------------------------------

# Side-channel for Get-TcUnitDefaultXmlPath ambiguity warnings.
# A returned path is always a single string; when the resolver finds
# multiple UmRT candidates, the chosen path is the freshest by
# LastWriteTime and a human-readable warning is stashed here so the
# caller can include it in the response payload. Cleared at the start
# of each Get-TcUnitDefaultXmlPath call.
$script:LastResolveWarning = ''

function Get-TcUnitDefaultXmlPath {
    <#
    .SYNOPSIS
        Absolute path that TcUnit's xUnit publisher writes to by default
        for a PLC running at the given runtime port.

    .DESCRIPTION
        Mirrors GVL_Param_TcUnit.xUnitFilePath, whose default is
        '%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml'. TwinCAT expands
        TC_BOOTPRJPATH per running PLC instance, and the expansion is
        runtime-kind dependent:

          - Kernel runtime (TcRTime):
              C:\TwinCAT\3.1\Boot\Plc\Port_<port>\
          - User-mode runtime (UmRT_<name>):
              C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes\<name>\3.1\Boot\
            (no Plc\Port_<port>\ subdirectory)

        Resolution order (first hit wins):

          1. $env:TCKIT_TCUNIT_XML_PATH if set (operator escape hatch).
          2. Kernel-RT path if the file exists.
          3. UmRT glob under %ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\.
             - 1 candidate: returned.
             - >1: most-recently-modified returned; alternatives reported
               via Get-TcUnitXmlResolveWarning.
          4. Fallback: kernel-RT path string (even if missing) so the
             downstream "not found at <path>" error in Get-TcUnitResults
             still fires with a stable shape.

        The AMS Net ID cannot narrow UmRT candidates; the local route
        127.0.0.1.1.1 is per-host, not per-runtime, and the runtime
        name lives only in the on-disk path. mtime is the only reliable
        freshness signal on the host side; the just-run XML is always
        the freshest match because Invoke-TcUnitRun waits on its mtime.
    #>
    param([int]$Port = $script:TcUnitDefaultPlcPort)

    $script:LastResolveWarning = ''

    if ($env:TCKIT_TCUNIT_XML_PATH) {
        return $env:TCKIT_TCUNIT_XML_PATH
    }

    $kernelPath = "C:\TwinCAT\3.1\Boot\Plc\Port_$Port\$script:TcUnitDefaultXmlFileName"
    if (Test-Path -LiteralPath $kernelPath) {
        return $kernelPath
    }

    if ($env:ProgramData) {
        $umrtGlob = Join-Path $env:ProgramData "Beckhoff\TwinCAT\3.1\Runtimes\*\3.1\Boot\$script:TcUnitDefaultXmlFileName"
        $candidates = @(Get-ChildItem -Path $umrtGlob -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($candidates.Count -eq 1) {
            return $candidates[0].FullName
        }
        if ($candidates.Count -gt 1) {
            $others = ($candidates | Select-Object -Skip 1 | ForEach-Object { $_.FullName }) -join '; '
            $script:LastResolveWarning = "Multiple UmRT runtimes published TcUnit XML; using freshest. Set TCKIT_TCUNIT_XML_PATH to pin. Alternatives: $others"
            return $candidates[0].FullName
        }
    }

    return $kernelPath
}

function Get-TcUnitXmlResolveWarning {
    <#
    .SYNOPSIS
        Last ambiguity warning recorded by Get-TcUnitDefaultXmlPath, or
        empty string if the last call was unambiguous. Read-only.
    #>
    if ($null -eq $script:LastResolveWarning) { return '' }
    return $script:LastResolveWarning
}

function Resolve-TcUnitXmlCandidates {
    <#
    .SYNOPSIS
        Enumerate all candidate TcUnit XML paths considered by
        Get-TcUnitDefaultXmlPath, with existence info. Used by
        tckit doctor's TcUnit section and the /tcunit-xml-resolve
        bridge route.

    .OUTPUTS
        @{
            env_override    = <string or $null>
            env_exists      = <bool>
            kernel_path     = <string>
            kernel_exists   = <bool>
            umrt_candidates = @(@{ path = <string>; mtime = <DateTime> }, ...)
              # sorted by mtime descending
        }
    #>
    param([int]$Port = $script:TcUnitDefaultPlcPort)

    $envOverride = $null
    $envExists   = $false
    if ($env:TCKIT_TCUNIT_XML_PATH) {
        $envOverride = $env:TCKIT_TCUNIT_XML_PATH
        $envExists   = [bool](Test-Path -LiteralPath $envOverride)
    }

    $kernelPath   = "C:\TwinCAT\3.1\Boot\Plc\Port_$Port\$script:TcUnitDefaultXmlFileName"
    $kernelExists = [bool](Test-Path -LiteralPath $kernelPath)

    $umrtCandidates = @()
    if ($env:ProgramData) {
        $umrtGlob = Join-Path $env:ProgramData "Beckhoff\TwinCAT\3.1\Runtimes\*\3.1\Boot\$script:TcUnitDefaultXmlFileName"
        $umrtCandidates = @(
            Get-ChildItem -Path $umrtGlob -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                ForEach-Object { @{ path = $_.FullName; mtime = $_.LastWriteTime } }
        )
    }

    return @{
        env_override    = $envOverride
        env_exists      = $envExists
        kernel_path     = $kernelPath
        kernel_exists   = $kernelExists
        umrt_candidates = $umrtCandidates
    }
}

# ------------------------------------------------------------------
# TcUnit JUnit XML parser
# ------------------------------------------------------------------

function ConvertTo-TcUnitFailureRecord {
    <#
    .SYNOPSIS
        Map a JUnit <failure> element to the AssertFailure shape used by
        the Python adapter: @{ message; expected; actual; line }.
    #>
    param([Parameter(Mandatory)]$Node)

    $message = ''
    try { $message = [string]$Node.message } catch { }
    if (-not $message) { $message = [string]$Node.InnerText }
    $body = [string]$Node.InnerText

    $expected = ''
    $actual   = ''
    $line     = 0

    $mexp = [regex]::Match($body, '(?i)expected\s*[:=]?\s*[''"]?([^\s,;''"]+)[''"]?')
    if ($mexp.Success) { $expected = $mexp.Groups[1].Value.Trim() }
    $mact = [regex]::Match($body, '(?i)(?:but\s*was|actual)\s*[:=]?\s*[''"]?([^\s,;''"]+)[''"]?')
    if ($mact.Success) { $actual = $mact.Groups[1].Value.Trim() }

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

function ConvertTo-TcUnitTestCaseRecord {
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
    $failures = @($failureNodes | ForEach-Object { ConvertTo-TcUnitFailureRecord -Node $_ })

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

function ConvertTo-TcUnitTestSuiteRecord {
    param(
        [Parameter(Mandatory)]$Node,
        [bool]$FailuresOnly = $false
    )

    $name = ''
    try { $name = [string]$Node.name } catch { }

    $tests = @()
    if ($Node.HasChildNodes) {
        foreach ($child in $Node.ChildNodes) {
            if ($child.LocalName -eq 'testcase') {
                $record = ConvertTo-TcUnitTestCaseRecord -Node $child
                if ($FailuresOnly -and $record.passed) { continue }
                $tests += $record
            }
        }
    }

    return @{
        name  = $name
        tests = $tests
    }
}

function Get-TcUnitIntAttribute {
    param($Node, [string]$Name, [int]$Default = 0)
    try {
        if ($Node.HasAttribute($Name)) {
            return [int]([string]$Node.GetAttribute($Name))
        }
    } catch { }
    return $Default
}

function Get-TcUnitDoubleAttribute {
    param($Node, [string]$Name, [double]$Default = 0.0)
    try {
        if ($Node.HasAttribute($Name)) {
            return [double]([string]$Node.GetAttribute($Name))
        }
    } catch { }
    return $Default
}

function ConvertFrom-TcUnitXml {
    <#
    .SYNOPSIS
        Parse a TcUnit JUnit-style XML file into the structured shape
        the Python adapter consumes.

    .DESCRIPTION
        Returns a hashtable with `suites`, `summary`, `failures`, and
        `xml_path` (success path) or `success=false` plus `error` on
        any parse failure. The shape matches what /results has always
        returned, plus a top-level `failures` flat list for callers
        that only want the lean view (e.g. /tcunit-run with
        IncludeResults=true).

        FailuresOnly:
          - $false (default): suites contain every testcase (passing
            and failing). Full per-test detail.
          - $true:            suites contain only failing testcases.
            Passing tests are omitted entirely. Use when the caller
            is going to surface this inline (run_tests with
            wait_for_results=True) and wants to keep payload bounded
            on large green suites.

        `summary` totals always reflect the *full* run; the suites
        list narrows but the counts do not.

        See ADR-0011.
    #>
    param(
        [Parameter(Mandatory)][string]$XmlPath,
        [bool]$FailuresOnly = $false
    )

    if (-not (Test-Path -LiteralPath $XmlPath)) {
        return @{ success = $false; error = "TcUnit results XML not found at $XmlPath."; xml_path = $XmlPath }
    }

    try {
        [xml]$doc = Get-Content -LiteralPath $XmlPath -Raw

        $root = $doc.DocumentElement
        if ($null -eq $root) {
            return @{ success = $false; error = "TcUnit results XML at $XmlPath has no root element."; xml_path = $XmlPath }
        }

        $suiteNodes = @()
        if ($root.LocalName -eq 'testsuites') {
            $suiteNodes = @($root.SelectNodes('testsuite'))
        } elseif ($root.LocalName -eq 'testsuite') {
            $suiteNodes = @($root)
        } else {
            return @{ success = $false; error = "Unexpected root element '$($root.LocalName)' in $XmlPath (expected testsuites or testsuite)."; xml_path = $XmlPath }
        }

        # Build the full suite/test tree first so summary totals always
        # cover the whole run; then narrow to failures-only on the way
        # out if requested.
        $fullSuites = @($suiteNodes | ForEach-Object {
            ConvertTo-TcUnitTestSuiteRecord -Node $_ -FailuresOnly $false
        })

        $summarySource = if ($root.LocalName -eq 'testsuites') { $root } else { $null }

        $summary = @{
            suites           = $fullSuites.Count
            tests            = 0
            asserts          = 0
            failures         = 0
            errors           = 0
            duration_seconds = 0.0
        }
        if ($null -ne $summarySource) {
            $summary.tests            = Get-TcUnitIntAttribute -Node $summarySource -Name 'tests'
            $summary.failures         = Get-TcUnitIntAttribute -Node $summarySource -Name 'failures'
            $summary.errors           = Get-TcUnitIntAttribute -Node $summarySource -Name 'errors'
            $summary.duration_seconds = Get-TcUnitDoubleAttribute -Node $summarySource -Name 'time'
        }
        if ($summary.tests -eq 0) {
            foreach ($s in $fullSuites) { $summary.tests += $s.tests.Count }
        }
        foreach ($s in $fullSuites) {
            foreach ($t in $s.tests) { $summary.asserts += $t.asserts }
        }
        if ($summary.failures -eq 0) {
            foreach ($s in $fullSuites) {
                foreach ($t in $s.tests) { if (-not $t.passed) { $summary.failures += 1 } }
            }
        }

        # Flat failures list for callers that want only the actionable
        # signal (model in a test loop): one entry per failed testcase
        # with its suite name. Lean shape — message only, no expected/
        # actual/line — to keep the inline payload bounded on large
        # red runs. The full per-test detail (including passes) stays
        # reachable via /results.
        $failuresFlat = @()
        foreach ($s in $fullSuites) {
            foreach ($t in $s.tests) {
                if (-not $t.passed) {
                    $msg = ''
                    if ($t.failures.Count -gt 0) {
                        $msg = [string]$t.failures[0].message
                    }
                    $failuresFlat += @{
                        suite_name = $s.name
                        test_name  = $t.name
                        message    = $msg
                    }
                }
            }
        }

        if ($FailuresOnly) {
            $suitesOut = @()
            foreach ($s in $fullSuites) {
                $narrowed = $s.tests | Where-Object { -not $_.passed }
                if ($narrowed -and @($narrowed).Count -gt 0) {
                    $suitesOut += @{ name = $s.name; tests = @($narrowed) }
                }
            }
        } else {
            $suitesOut = $fullSuites
        }

        return @{
            success  = $true
            suites   = $suitesOut
            summary  = $summary
            failures = $failuresFlat
            xml_path = $XmlPath
        }
    }
    catch {
        return @{ success = $false; error = $_.Exception.Message; xml_path = $XmlPath }
    }
}

# ------------------------------------------------------------------
# File freshness
# ------------------------------------------------------------------

function Wait-TcFileFresh {
    <#
    .SYNOPSIS
        Wait for a file to appear with a LastWriteTime strictly newer than
        a known epoch. Used to confirm TcUnit has finished writing its XML
        after the suites-finished flag flipped to true.

    .OUTPUTS
        @{ success = bool; mtime = DateTime; elapsed_ms = int }
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][datetime]$After,
        [int]$TimeoutMs = 5000,
        [int]$PollIntervalMs = 100
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-Path -LiteralPath $Path) {
            $info = Get-Item -LiteralPath $Path
            if ($info.LastWriteTime -gt $After) {
                $sw.Stop()
                return @{ success = $true; mtime = $info.LastWriteTime; elapsed_ms = [int]$sw.ElapsedMilliseconds }
            }
        }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    $sw.Stop()
    return @{ success = $false; mtime = $null; elapsed_ms = [int]$sw.ElapsedMilliseconds }
}

# ------------------------------------------------------------------
# Module exports
# ------------------------------------------------------------------

Export-ModuleMember -Function `
    Wait-TcSymbolEquals, `
    Get-TcUnitDefaultXmlPath, `
    Get-TcUnitXmlResolveWarning, `
    Resolve-TcUnitXmlCandidates, `
    ConvertFrom-TcUnitXml, `
    Wait-TcFileFresh
