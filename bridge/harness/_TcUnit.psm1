<#
.SYNOPSIS
    TcUnit-specific helpers: live-runtime symbol reads via ADS, the
    TcUnit_ResultExportXmlPath constant lookup, and file-freshness polling.

.DESCRIPTION
    Sibling module to _TcDte.psm1. _TcDte owns DTE / project-tree navigation;
    this module owns concerns that touch the running runtime (ADS) or the
    TcUnit project convention. Exported functions:

      Get-TcAdsAssembly       — locate and load TwinCAT.Ads.dll once per session
      Connect-TcAdsClient     — open an ADS connection to a target's PLC port
      Get-TcSymbolValue       — read a single ADS symbol value
      Wait-TcSymbolEquals     — poll a symbol until it equals a value or times out
      Get-TcUnitXmlPath       — resolve the TcUnit_ResultExportXmlPath constant
                                 from the test project (falls back to default)
      Wait-TcFileFresh        — wait for a file to appear with mtime > a given epoch
#>

Set-StrictMode -Version Latest

# ------------------------------------------------------------------
# Defaults
# ------------------------------------------------------------------

$script:TcUnitDefaultXmlPath = 'C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml'
$script:TcUnitPlcRuntimePort = 851
$script:TcAdsAssemblyLoaded  = $false

# ------------------------------------------------------------------
# ADS assembly + client
# ------------------------------------------------------------------

function Get-TcAdsAssembly {
    <#
    .SYNOPSIS
        Load TwinCAT.Ads.dll once per session. Honours $env:TCADS_DLL_PATH.
    #>
    if ($script:TcAdsAssemblyLoaded) { return }

    $candidates = @()
    if ($env:TCADS_DLL_PATH) { $candidates += $env:TCADS_DLL_PATH }
    $candidates += @(
        'C:\TwinCAT\AdsApi\.NET\v4.0\TwinCAT.Ads.dll',
        'C:\TwinCAT\AdsApi\TcAdsDll\.NET\v4.0\TwinCAT.Ads.dll',
        'C:\TwinCAT\3.1\Components\Plc\TwinCAT.Ads.dll'
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            Add-Type -Path $path
            $script:TcAdsAssemblyLoaded = $true
            return
        }
    }
    throw "TwinCAT.Ads.dll not found. Tried: $($candidates -join '; '). Set TCADS_DLL_PATH to override."
}

function Connect-TcAdsClient {
    <#
    .SYNOPSIS
        Open a TwinCAT.Ads.AdsClient connected to the PLC runtime on the
        target NetId. Caller MUST Disconnect()/Dispose() — wrap in try/finally.
    #>
    param(
        [Parameter(Mandatory)][string]$TargetAmsId,
        [int]$Port = $script:TcUnitPlcRuntimePort
    )
    Get-TcAdsAssembly
    $client = New-Object 'TwinCAT.Ads.AdsClient'
    $client.Connect($TargetAmsId, $Port)
    return $client
}

# ------------------------------------------------------------------
# Symbol reads
# ------------------------------------------------------------------

function Get-TcSymbolValue {
    <#
    .SYNOPSIS
        Read a single ADS symbol value. ``$Type`` is a .NET type, e.g.
        ``[bool]``, ``[uint32]``, ``[int]``.

    .EXAMPLE
        Get-TcSymbolValue -Client $c -Symbol 'TcUnit.G_TestRunner.bTestSuitesFinished' -Type ([bool])
    #>
    param(
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string]$Symbol,
        [Parameter(Mandatory)][type]$Type
    )
    return $Client.ReadValue($Symbol, $Type)
}

function Wait-TcSymbolEquals {
    <#
    .SYNOPSIS
        Poll a symbol until it equals the expected value or timeout expires.

    .OUTPUTS
        @{ success = bool; value = <last read>; elapsed_ms = int }
    #>
    param(
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string]$Symbol,
        [Parameter(Mandatory)][type]$Type,
        [Parameter(Mandatory)]$Expected,
        [int]$TimeoutMs = 120000,
        [int]$PollIntervalMs = 500
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $last = Get-TcSymbolValue -Client $Client -Symbol $Symbol -Type $Type
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

        This reads the compile-time constant text, not a runtime symbol.
        Robust across runtime states (pre-Run, Run, Config).

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
        after the suites flag flipped to true.

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
    Get-TcAdsAssembly, Connect-TcAdsClient, `
    Get-TcSymbolValue, Wait-TcSymbolEquals, `
    Get-TcUnitXmlPath, Wait-TcFileFresh
