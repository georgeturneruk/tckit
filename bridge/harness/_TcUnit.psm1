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
      Wait-TcSymbolEquals       — poll a PLC symbol on a TcSession until it
                                   equals a value or times out
      Get-TcUnitDefaultXmlPath  — return the xUnit publisher's default
                                   output path for a given PLC port
      Wait-TcFileFresh          — wait for a file to appear with mtime > a
                                   given epoch
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

        Env var override: TCKIT_TCUNIT_XML_PATH points at the absolute
        file path on this machine. Set as a real env var (or in a local
        .env) — the kernel-runtime default below is wrong on a UmRT
        bench, so the operator must declare the path explicitly per
        machine.

        Callers that also override xUnitFilePath via library parameters
        must pass the resolved path to /tcunit-run / /results
        explicitly; the bridge does not read xUnitFilePath off the
        running runtime today.
    #>
    param([int]$Port = $script:TcUnitDefaultPlcPort)
    if ($env:TCKIT_TCUNIT_XML_PATH) {
        return $env:TCKIT_TCUNIT_XML_PATH
    }
    return "C:\TwinCAT\3.1\Boot\Plc\Port_$Port\$script:TcUnitDefaultXmlFileName"
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
    Wait-TcFileFresh
