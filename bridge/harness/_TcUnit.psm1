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
    Wait-TcFileFresh
