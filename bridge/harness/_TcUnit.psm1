<#
.SYNOPSIS
    TcUnit-specific helpers: the TcUnit_ResultExportXmlPath constant lookup,
    a session-based symbol-equality poll, and file-freshness polling.

.DESCRIPTION
    Sibling module to _TcDte.psm1. _TcDte owns DTE / project-tree navigation;
    this module owns concerns that are specific to the TcUnit runner.

    Runtime ADS work (symbol reads, runtime-state transitions) goes through
    Beckhoff's official TcXaeMgmt module. Callers create a TcSession via
    New-TcSession from TcXaeMgmt and pass it to Wait-TcSymbolEquals here.

    Exported functions:
      Wait-TcSymbolEquals    — poll a PLC symbol on a TcSession until it
                                equals a value or times out
      Get-TcUnitXmlPath      — resolve the TcUnit_ResultExportXmlPath
                                constant from the test project's GVL
                                declarations (falls back to default)
      Wait-TcFileFresh       — wait for a file to appear with mtime > a
                                given epoch
#>

Set-StrictMode -Version Latest

# ------------------------------------------------------------------
# Defaults
# ------------------------------------------------------------------

$script:TcUnitDefaultXmlPath = 'C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml'

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
# TcUnit project convention
# ------------------------------------------------------------------

function Get-TcUnitXmlPath {
    <#
    .SYNOPSIS
        Resolve the absolute path to TcUnitResults.xml from the test PLC
        project's GVL declarations.

    .DESCRIPTION
        Walks the PLC project tree for a GVL whose declaration text
        contains ``TcUnit_ResultExportXmlPath : T_MaxString := '<path>'``.
        Returns the string literal between the single quotes if found,
        otherwise returns the canonical default
        ``C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml``.

        Compile-time constant text, not a runtime symbol read — robust
        across pre-Run / Run / Config states.

    .PARAMETER PlcNode
        The PLC project tree item (TIPC^<plc>^<plc> Project). Get one via
        Get-TcPlcProjectNode from _TcDte.psm1.
    #>
    param([Parameter(Mandatory)]$PlcNode)

    $pattern = '(?im)^\s*TcUnit_ResultExportXmlPath\s*:\s*T_MaxString\s*:=\s*''([^'']+)''\s*;'

    $stack = New-Object System.Collections.Stack
    $stack.Push($PlcNode)
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        try {
            if ($node.ChildCount -lt 1) { continue }
        } catch { continue }
        for ($i = 1; $i -le $node.ChildCount; $i++) {
            $child = $node.Child($i)
            $decl = ''
            try { $decl = [string]$child.DeclarationText } catch { $decl = '' }
            if ($decl) {
                $m = [regex]::Match($decl, $pattern)
                if ($m.Success) { return $m.Groups[1].Value }
            }
            $stack.Push($child)
        }
    }
    return $script:TcUnitDefaultXmlPath
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
    Get-TcUnitXmlPath, `
    Wait-TcFileFresh
