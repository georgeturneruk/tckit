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

try {
    # ----------------------------------------------------------------
    # Resolve XML path — default to the xUnit publisher's standard
    # output. Callers that override GVL_Param_TcUnit.xUnitFilePath via
    # library parameters must pass -XmlPath explicitly. /results always
    # returns the FULL per-test list (passing tests included);
    # /tcunit-run with IncludeResults=true uses the failures-only path.
    # ----------------------------------------------------------------
    if (-not $XmlPath) {
        $XmlPath = Get-TcUnitDefaultXmlPath
    }

    $resolveWarning = Get-TcUnitXmlResolveWarning
    $parsed = ConvertFrom-TcUnitXml -XmlPath $XmlPath -FailuresOnly $false

    if (-not $parsed.success) {
        $parsed.resolve_warning = $resolveWarning
        return $parsed
    }

    $parsed.resolve_warning = $resolveWarning
    return $parsed
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
