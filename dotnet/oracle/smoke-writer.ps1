<#
.SYNOPSIS
    Self-cleaning live-COM smoke for the C# writer lane (ADR-0015). Drives every
    authoring verb in dependency order against a throwaway scratch solution on a
    live 4026 (TcXaeShell attached), asserts each Result.success, and tears the
    scratch project down. This is the gate that promotes a verb from [~] to [x]
    in dotnet/PORTING.md.

.DESCRIPTION
    Unlike the parity oracle (compare.ps1, reader lane) this needs a real XAE:
    the verbs go through the COM Automation Interface. It exercises the whole
    surface end to end: scaffold a solution + two PLCs, author POUs / GVL / DUT /
    method / property / variable, run the update + patch verbs, save the first PLC
    as an installed library, reference it (and a placeholder, with parameters)
    from the second PLC, then delete everything in reverse.

    Destructive to the current XAE solution state: it creates and opens a scratch
    solution. Run it knowingly on the bench box, not against a project you care
    about. The scratch directory is removed on the way out (best-effort; XAE may
    still hold a handle, in which case delete it by hand after closing XAE).

.PARAMETER Root
    Directory to create the scratch solution under. Defaults to a temp folder.

.PARAMETER KeepScratch
    Leave the scratch solution on disk (skip teardown) for post-mortem inspection.

.EXAMPLE
    pwsh dotnet/oracle/smoke-writer.ps1
    pwsh dotnet/oracle/smoke-writer.ps1 -Root C:\tmp\tckit-smoke -KeepScratch
#>
param(
    [string]$Root = (Join-Path $env:TEMP ("tckit-writer-smoke-" + [Guid]::NewGuid().ToString('N'))),
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$dotnet = if (Test-Path 'C:\dn8\dotnet.exe') { 'C:\dn8\dotnet.exe' } else { 'dotnet' }
$cliProject = "$repoRoot\dotnet\src\TcKit.Cli"

$script:pass = 0
$script:fail = 0
$script:failedVerbs = @()

# Build once so each verb call uses --no-build (no MSBuild chatter on stdout).
Write-Host "Building CLI..." -ForegroundColor Cyan
& $dotnet build $cliProject -c Debug --nologo -v q | Out-Null

function Invoke-Verb {
    <#
      Run one writer verb through the CLI; assert Result.success unless -ExpectFail.
      The CLI prints exactly one JSON line (the Result); take the last non-empty line.
    #>
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$VerbArgs,
        [switch]$ExpectFail
    )

    $raw = (& $dotnet 'run' '--project' $cliProject '--no-build' '--' @VerbArgs 2>&1) | Out-String
    $line = ($raw.Trim() -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)
    $res = $null
    try { $res = $line | ConvertFrom-Json } catch { }

    $success = ($null -ne $res) -and $res.PSObject.Properties.Name -contains 'success' -and $res.success
    $wanted = if ($ExpectFail) { -not $success } else { $success }

    if ($wanted) {
        $script:pass++
        Write-Host ("PASS  {0}" -f $Label) -ForegroundColor Green
    } else {
        $script:fail++
        $script:failedVerbs += $Label
        Write-Host ("FAIL  {0}" -f $Label) -ForegroundColor Red
        Write-Host ("      {0}" -f $line) -ForegroundColor DarkGray
    }
}

# Code-bearing args are passed as @<file>; write the fixtures up front.
New-Item -ItemType Directory -Path $Root -Force | Out-Null
$codeDir = Join-Path $Root '_code'
New-Item -ItemType Directory -Path $codeDir -Force | Out-Null
function New-Code([string]$name, [string]$body) {
    $p = Join-Path $codeDir $name
    Set-Content -LiteralPath $p -Value $body -Encoding utf8
    return $p
}

$fbCode      = New-Code 'fb.st'        "FUNCTION_BLOCK FB_Smoke`nVAR`n    nCount : INT;`nEND_VAR`nnCount := nCount + 1;"
$gvlCode     = New-Code 'gvl.st'       "{attribute 'qualified_only'}`nVAR_GLOBAL`n    gActive : BOOL;`nEND_VAR"
$dutCode     = New-Code 'dut.st'       "TYPE ST_Smoke :`nSTRUCT`n    a : INT;`n    b : LREAL;`nEND_STRUCT`nEND_TYPE"
$methodCode  = New-Code 'method.st'    "METHOD Step : BOOL`nVAR_INPUT`n    dt : LREAL;`nEND_VAR`nStep := TRUE;"
$getCode     = New-Code 'get.st'       "Value := nCount;"
$setCode     = New-Code 'set.st'       "nCount := Value;"
$newDecl     = New-Code 'decl.st'      "FUNCTION_BLOCK FB_Smoke`nVAR`n    nCount : INT;`n    bDone : BOOL;`nEND_VAR"
$newImpl     = New-Code 'impl.st'      "nCount := nCount + 2;`nbDone := nCount > 10;"
$newMethod   = New-Code 'method2.st'   "METHOD Step : BOOL`nVAR_INPUT`n    dt : LREAL;`nEND_VAR`nStep := dt > 0.0;"
$patchOld    = New-Code 'patchold.st'  "nCount := nCount + 2;"
$patchNew    = New-Code 'patchnew.st'  "nCount := nCount + 3;"

$slnPath = Join-Path $Root 'Smoke.sln'
$libPath = Join-Path $Root 'Smoke_Plc.library'
$plc1 = 'Smoke_Plc'
$plc2 = 'Smoke2'
$paramsJson = '{"GVL_Param_Tc3_Module":{"bExample":"TRUE"}}'

Write-Host "`n=== Scaffolding ===" -ForegroundColor Cyan
Invoke-Verb 'create-project'            @('create-project', 'Smoke', $Root)
Invoke-Verb 'add-plc-project'           @('add-plc-project', $plc2, '--sln', $slnPath)

Write-Host "`n=== Authoring (PLC 1) ===" -ForegroundColor Cyan
Invoke-Verb 'add-folder'                @('add-folder', 'Drives', '--plc', $plc1)
Invoke-Verb 'add-pou'                   @('add-pou', 'FB_Smoke', 'function_block', "@$fbCode", '--plc', $plc1)
Invoke-Verb 'add-gvl'                   @('add-gvl', 'GVL_Smoke', "@$gvlCode", '--plc', $plc1)
Invoke-Verb 'add-dut'                   @('add-dut', 'ST_Smoke', "@$dutCode", '--kind', 'struct', '--plc', $plc1)
Invoke-Verb 'add-method'                @('add-method', 'FB_Smoke', 'Step', "@$methodCode", '--plc', $plc1)
Invoke-Verb 'add-property'              @('add-property', 'FB_Smoke', 'Value', 'INT', '--get', "@$getCode", '--set', "@$setCode", '--plc', $plc1)
Invoke-Verb 'add-variable'              @('add-variable', 'FB_Smoke', 'VAR_INPUT', 'bEnable : BOOL;', '--plc', $plc1)

Write-Host "`n=== Updates / patches ===" -ForegroundColor Cyan
Invoke-Verb 'update-pou-declaration'    @('update-pou-declaration', 'FB_Smoke', "@$newDecl", '--plc', $plc1)
Invoke-Verb 'update-pou-implementation' @('update-pou-implementation', 'FB_Smoke', "@$newImpl", '--plc', $plc1)
Invoke-Verb 'update-method-body'        @('update-method-body', 'FB_Smoke', 'Step', "@$newMethod", '--plc', $plc1)
Invoke-Verb 'update-pou-implementation-patch' @('update-pou-implementation-patch', 'FB_Smoke', "@$patchOld", "@$patchNew", '--plc', $plc1)

Write-Host "`n=== Library lane ===" -ForegroundColor Cyan
Invoke-Verb 'save-plc-as-library'       @('save-plc-as-library', $libPath, '--overwrite', '--plc', $plc1)
Invoke-Verb 'add-library-reference'     @('add-library-reference', $plc1, '--version', '*', '--distributor', 'Tc3 Project', '--plc', $plc2)
Invoke-Verb 'add-library-placeholder'   @('add-library-placeholder', 'Tc3_Module', 'Tc3_Module', '--distributor', 'Beckhoff Automation GmbH', '--plc', $plc2)
Invoke-Verb 'set-placeholder-parameters' @('set-placeholder-parameters', 'Tc3_Module', $paramsJson, '--plc', $plc2)

Write-Host "`n=== Teardown (reverse) ===" -ForegroundColor Cyan
Invoke-Verb 'delete-placeholder'        @('delete-placeholder', 'Tc3_Module', '--plc', $plc2)
Invoke-Verb 'delete-library-reference'  @('delete-library-reference', $plc1, '--version', '*', '--distributor', 'Tc3 Project', '--plc', $plc2)
Invoke-Verb 'delete-variable'           @('delete-variable', 'FB_Smoke', 'bEnable', '--plc', $plc1)
Invoke-Verb 'delete-property'           @('delete-property', 'FB_Smoke', 'Value', '--plc', $plc1)
Invoke-Verb 'delete-method'             @('delete-method', 'FB_Smoke', 'Step', '--plc', $plc1)
Invoke-Verb 'delete-dut'                @('delete-dut', 'ST_Smoke', '--plc', $plc1)
Invoke-Verb 'delete-gvl'                @('delete-gvl', 'GVL_Smoke', '--plc', $plc1)
Invoke-Verb 'delete-pou'                @('delete-pou', 'FB_Smoke', '--plc', $plc1)
Invoke-Verb 'delete-folder'             @('delete-folder', 'Drives', '--plc', $plc1)

if (-not $KeepScratch) {
    Write-Host "`nRemoving scratch solution at $Root ..." -ForegroundColor Cyan
    try {
        Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "  (could not remove; XAE may still hold the solution open) $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ("`n==== {0} passed, {1} failed ====" -f $script:pass, $script:fail) `
    -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) {
    Write-Host ("Failed: {0}" -f ($script:failedVerbs -join ', ')) -ForegroundColor Red
    exit 1
}
exit 0
