<#
.SYNOPSIS
    Live COM + ADS validation of the build / test / deploy lane (ADR-0015) against a bench TcUnit
    fixture on a real 4026. Drives the bench cycle through the C# CLI verbs:

        open-project -> save-plc-as-library (library, install) -> build (tests)
        -> deploy (tests) -> start-runtime -> run-tests (tests) -> get-test-results

    and asserts each step. This is the gate that flips the Build/test/deploy section of PORTING.md
    to [x]. deploy + start-runtime RESTART TwinCAT on the target; run it knowingly.

.PARAMETER Target
    Target AMS Net ID. Defaults to TARGET_AMS_ID env / the bench target.

.PARAMETER Sln
    The TcUnit fixture solution. Defaults to the B1 rolling-average fixture.

.EXAMPLE
    pwsh dotnet/oracle/smoke-build-test.ps1 -Target 192.168.0.142.1.1
#>
param(
    [string]$Target = $(if ($env:TARGET_AMS_ID) { $env:TARGET_AMS_ID } else { '192.168.0.142.1.1' }),
    [string]$Sln = "$PSScriptRoot\..\..\bench\fixtures\bug-hunting\B1-off-by-one\B1RollingAverage.sln",
    [string]$LibraryPlc = 'B1RollingAverage_Plc',
    [string]$TestsPlc = 'RollingAverageTests'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$dotnet = if (Test-Path 'C:\dn8\dotnet.exe') { 'C:\dn8\dotnet.exe' } else { 'dotnet' }
$cliProject = "$repoRoot\dotnet\src\TcKit.Cli"
$Sln = (Resolve-Path $Sln).Path
$libraryOut = Join-Path $env:TEMP "$LibraryPlc.library"

Write-Host "Building CLI..." -ForegroundColor Cyan
& $dotnet build $cliProject -c Debug --nologo -v q | Out-Null

function Invoke-Verb {
    param([Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][string[]]$VerbArgs, [switch]$Fatal)

    $raw = (& $dotnet 'run' '--project' $cliProject '--no-build' '--' @VerbArgs 2>&1) | Out-String
    $res = $null
    $start = $raw.IndexOf('{')
    if ($start -ge 0) { try { $res = $raw.Substring($start) | ConvertFrom-Json } catch { } }
    $ok = ($null -ne $res) -and ($res.PSObject.Properties.Name -contains 'success') -and $res.success

    if ($ok) {
        Write-Host ("PASS  {0}" -f $Label) -ForegroundColor Green
    } else {
        $detail = if ($null -ne $res -and $res.PSObject.Properties.Name -contains 'error') { $res.error } else { $raw.Trim() }
        Write-Host ("FAIL  {0}" -f $Label) -ForegroundColor Red
        Write-Host ("      {0}" -f $detail) -ForegroundColor DarkGray
        if ($Fatal) { throw "Fatal step failed: $Label" }
    }
    return $res
}

Write-Host "`n=== Build / test / deploy lane against $Target ===" -ForegroundColor Cyan
Write-Host "Fixture: $Sln" -ForegroundColor DarkGray

Invoke-Verb 'open-project'         @('open-project', $Sln) -Fatal | Out-Null

# Enable the xUnit publisher on the tests PLC so run-tests / get-test-results have XML to parse
# (TcUnit defaults xUnitEnablePublish to FALSE). Passed via a file to dodge PowerShell quote mangling.
$paramsFile = Join-Path $env:TEMP "tcunit-publish-$([Guid]::NewGuid().ToString('N')).json"
Set-Content -LiteralPath $paramsFile -Value '{"GVL_Param_TcUnit":{"xUnitEnablePublish":"TRUE"}}' -Encoding utf8
Invoke-Verb 'set-placeholder-parameters' @('set-placeholder-parameters', 'TcUnit', "@$paramsFile", '--plc', $TestsPlc) | Out-Null

Invoke-Verb 'save-plc-as-library'  @('save-plc-as-library', $libraryOut, '--overwrite', '--plc', $LibraryPlc) -Fatal | Out-Null
Invoke-Verb 'build'                @('build', '--plc', $TestsPlc) -Fatal | Out-Null
Invoke-Verb 'deploy'               @('deploy', $Target, '--plc', $TestsPlc) -Fatal | Out-Null
Invoke-Verb 'start-runtime'        @('start-runtime', $Target) -Fatal | Out-Null

$run = Invoke-Verb 'run-tests'     @('run-tests', $Target, '--plc', $TestsPlc, '--timeout', '120')
if ($null -ne $run -and $run.success) {
    Write-Host ("      summary: {0} suites, {1} tests, {2} failures (xml_published={3})" -f `
        $run.summary.suites, $run.summary.tests, $run.summary.failures, $run.xml_published) -ForegroundColor DarkGray
}

$results = Invoke-Verb 'get-test-results' @('get-test-results', $Target)
if ($null -ne $results -and $results.success) {
    Write-Host ("      results: {0} suites parsed, {1} failures" -f `
        $results.suites.Count, $results.summary.failures) -ForegroundColor DarkGray
}

Write-Host "`nNote: the fixture's library .plcproj may have a ProjectInfo metadata change from" -ForegroundColor Yellow
Write-Host "save-plc-as-library; 'git checkout -- bench/fixtures' to discard if so." -ForegroundColor Yellow
