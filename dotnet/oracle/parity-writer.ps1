<#
.SYNOPSIS
    Backend parity oracle for the writer lane (ADR-0017). Runs each authoring verb
    twice — once through the automation backend (live XAE) and once through the xml
    backend — against two clones of the same scratch solution, canonicalises both
    trees after every verb, and diffs them. The automation backend is the oracle:
    a green run means the xml backend's on-disk output is XAE-equivalent.

.DESCRIPTION
    Needs a real 4026 with TcXaeShell attached (the automation arm and the initial
    scaffold go through COM). Sequence:

      1. Scaffold a scratch solution via the automation backend (create-project).
      2. Clone the baseline tree to xae/ and xml/; reopen xae/ in XAE.
      3. For each verb: run it via --writer automation against xae/, via
         --writer xml (TCKIT_SOLUTION) against xml/, canonicalise, diff.
      4. Stop at the first divergence (default) or keep going with -Continue.

    Canonicalisation before the diff: BOM stripped, CRLF -> LF, object Id GUIDs
    dropped, LineIds elements dropped, .plcproj ProjectExtensions dropped and
    ItemGroup children sorted, and only TwinCAT source files compared (XAE's
    build artefacts — .tmc, compileinfo, .vs — are not part of the contract).

    Destructive to the current XAE solution state; run it knowingly on the bench
    box. Scratch is removed on the way out unless -KeepScratch.

.PARAMETER Root
    Directory to create the scratch trees under. Defaults to a temp folder.

.PARAMETER KeepScratch
    Leave the scratch trees on disk for post-mortem inspection.

.PARAMETER Continue
    Do not stop at the first diverging verb; report all divergences at the end.

.EXAMPLE
    pwsh dotnet/oracle/parity-writer.ps1
    pwsh dotnet/oracle/parity-writer.ps1 -Root C:\tmp\tckit-parity -KeepScratch -Continue
#>
param(
    [string]$Root = (Join-Path $env:TEMP ("tckit-writer-parity-" + [Guid]::NewGuid().ToString('N'))),
    [switch]$KeepScratch,
    [switch]$Continue
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$dotnet = if (Test-Path 'C:\dn8\dotnet.exe') { 'C:\dn8\dotnet.exe' } else { 'dotnet' }
$cliProject = "$repoRoot\dotnet\src\TcKit.Cli"

$script:pass = 0
$script:fail = 0
$script:diverged = @()

Write-Host "Building CLI..." -ForegroundColor Cyan
& $dotnet build $cliProject -c Debug --nologo -v q | Out-Null

# The multi-targeted CLI needs the framework named; the windows flavour serves both backends.
$runArgs = @('run', '--project', $cliProject, '--framework', 'net8.0-windows', '--no-build', '--')

function Invoke-Cli {
    param(
        [Parameter(Mandatory)][string[]]$VerbArgs,
        [hashtable]$Env = @{}
    )

    foreach ($key in $Env.Keys) { Set-Item -Path "env:$key" -Value $Env[$key] }
    try {
        $raw = (& $dotnet @runArgs @VerbArgs 2>&1) | Out-String
    } finally {
        foreach ($key in $Env.Keys) { Remove-Item -Path "env:$key" -ErrorAction SilentlyContinue }
    }

    $res = $null
    $start = $raw.IndexOf('{')
    if ($start -ge 0) {
        try { $res = $raw.Substring($start) | ConvertFrom-Json } catch { }
    }

    [pscustomobject]@{
        Success = ($null -ne $res) -and ($res.PSObject.Properties.Name -contains 'success') -and $res.success
        Raw     = $raw.Trim()
        Result  = $res
    }
}

# --- canonicalisation ---------------------------------------------------------

$sourceExtensions = @('.sln', '.tsproj', '.plcproj', '.TcPOU', '.TcIO', '.TcGVL', '.TcDUT', '.TcTTO')

function Write-CanonicalTree {
    <# Mirror a solution tree into $Out with every file in canonical form. #>
    param(
        [Parameter(Mandatory)][string]$Tree,
        [Parameter(Mandatory)][string]$Out
    )

    if (Test-Path $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
    New-Item -ItemType Directory -Path $Out -Force | Out-Null

    $treeFull = (Resolve-Path -LiteralPath $Tree).Path
    Get-ChildItem -LiteralPath $Tree -Recurse -File | ForEach-Object {
        if ($_.FullName -match '\\(\.vs|_code|bin|obj)\\') { return }
        if ($sourceExtensions -notcontains $_.Extension) { return }

        # Substring instead of Path.GetRelativePath: Windows PowerShell 5.1 lacks the API.
        $rel = $_.FullName.Substring($treeFull.Length).TrimStart('\', '/')
        $dest = Join-Path $Out $rel
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        Set-Content -LiteralPath $dest -Value (Convert-Canonical $_.FullName) -Encoding utf8 -NoNewline
    }
}

function Convert-Canonical {
    param([Parameter(Mandatory)][string]$Path)

    $text = [IO.File]::ReadAllText($Path)   # ReadAllText already strips the BOM
    $ext = [IO.Path]::GetExtension($Path)

    if ($ext -in @('.TcPOU', '.TcIO', '.TcGVL', '.TcDUT', '.TcTTO', '.tsproj', '.plcproj')) {
        $xml = [xml]$text
        Remove-NonContractNodes $xml.DocumentElement ($ext -ieq '.plcproj')
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Indent = $true
        $settings.IndentChars = '  '
        $settings.OmitXmlDeclaration = $true
        $sb = New-Object System.Text.StringBuilder
        $writer = [System.Xml.XmlWriter]::Create($sb, $settings)
        $xml.Save($writer)
        $writer.Close()
        $text = $sb.ToString()
    }

    return ($text -replace "`r`n", "`n").TrimEnd() + "`n"
}

function Remove-NonContractNodes {
    <# Drop Id attributes, LineIds; for .plcproj also ProjectExtensions, and sort ItemGroups. #>
    param(
        [Parameter(Mandatory)][System.Xml.XmlElement]$Root,
        [bool]$IsPlcProj
    )

    foreach ($node in @($Root.SelectNodes('//*[local-name()="LineIds" or local-name()="LineId"]'))) {
        $node.ParentNode.RemoveChild($node) | Out-Null
    }

    foreach ($node in @($Root.SelectNodes('//*[@Id]'))) {
        $node.RemoveAttribute('Id')
    }

    if ($IsPlcProj) {
        foreach ($node in @($Root.SelectNodes('//*[local-name()="ProjectExtensions"]'))) {
            $node.ParentNode.RemoveChild($node) | Out-Null
        }

        foreach ($group in @($Root.SelectNodes('//*[local-name()="ItemGroup"]'))) {
            $children = @($group.ChildNodes | Where-Object { $_.NodeType -eq 'Element' }) |
                Sort-Object { '{0}|{1}' -f $_.LocalName, $_.GetAttribute('Include') }
            foreach ($child in @($group.ChildNodes)) { $group.RemoveChild($child) | Out-Null }
            foreach ($child in $children) { $group.AppendChild($child) | Out-Null }
        }
    }
}

function Compare-Trees {
    param([Parameter(Mandatory)][string]$Label)

    $canonXae = Join-Path $Root '_canon-xae'
    $canonXml = Join-Path $Root '_canon-xml'
    Write-CanonicalTree -Tree (Join-Path $Root 'xae') -Out $canonXae
    Write-CanonicalTree -Tree (Join-Path $Root 'xml') -Out $canonXml

    & git diff --no-index --stat -- $canonXae $canonXml | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $script:pass++
        Write-Host ("PASS  {0}" -f $Label) -ForegroundColor Green
        return $true
    }

    $script:fail++
    $script:diverged += $Label
    Write-Host ("DIVERGED  {0}" -f $Label) -ForegroundColor Red
    & git --no-pager diff --no-index -- $canonXae $canonXml | Select-Object -First 80 | ForEach-Object {
        Write-Host ("  {0}" -f $_) -ForegroundColor DarkGray
    }

    return $false
}

function Invoke-Pair {
    <# Run one verb through both backends, then diff the canonicalised trees. #>
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$VerbArgs
    )

    $xae = Invoke-Cli -VerbArgs ($VerbArgs + @('--writer', 'automation'))
    $xml = Invoke-Cli -VerbArgs ($VerbArgs + @('--writer', 'xml')) -Env @{
        TCKIT_SOLUTION = (Join-Path $Root 'xml\Parity.sln')
    }

    if (-not $xae.Success -or -not $xml.Success) {
        $script:fail++
        $script:diverged += $Label
        Write-Host ("FAIL  {0}  (automation: {1}; xml: {2})" -f $Label, $xae.Success, $xml.Success) -ForegroundColor Red
        if (-not $xae.Success) { Write-Host ("  automation: {0}" -f $xae.Raw) -ForegroundColor DarkGray }
        if (-not $xml.Success) { Write-Host ("  xml:        {0}" -f $xml.Raw) -ForegroundColor DarkGray }
        return $false
    }

    # Soft check: matching Result detail keys (log-only; the tree diff is the hard gate).
    $xaeKeys = @($xae.Result.details.PSObject.Properties.Name) -join ','
    $xmlKeys = @($xml.Result.details.PSObject.Properties.Name) -join ','
    if ($xaeKeys -ne $xmlKeys) {
        Write-Host ("  note  {0}: detail keys differ (automation: {1} / xml: {2})" -f $Label, $xaeKeys, $xmlKeys) -ForegroundColor Yellow
    }

    return (Compare-Trees -Label $Label)
}

# --- scaffold -----------------------------------------------------------------

New-Item -ItemType Directory -Path $Root -Force | Out-Null
$codeDir = Join-Path $Root '_code'
New-Item -ItemType Directory -Path $codeDir -Force | Out-Null
function New-Code([string]$name, [string]$body) {
    $p = Join-Path $codeDir $name
    Set-Content -LiteralPath $p -Value $body -Encoding utf8
    return $p
}

$fbCode     = New-Code 'fb.st'      "FUNCTION_BLOCK FB_Parity`nVAR`n    nCount : INT;`nEND_VAR`nnCount := nCount + 1;"
$gvlCode    = New-Code 'gvl.st'     "{attribute 'qualified_only'}`nVAR_GLOBAL`n    gActive : BOOL;`nEND_VAR"
$dutCode    = New-Code 'dut.st'     "TYPE ST_Parity :`nSTRUCT`n    a : INT;`n    b : LREAL;`nEND_STRUCT`nEND_TYPE"
$methodCode = New-Code 'method.st'  "METHOD Step : BOOL`nVAR_INPUT`n    dt : LREAL;`nEND_VAR`nStep := TRUE;"
$getCode    = New-Code 'get.st'     "Value := nCount;"
$setCode    = New-Code 'set.st'     "nCount := Value;"
$newDecl    = New-Code 'decl.st'    "FUNCTION_BLOCK FB_Parity`nVAR_INPUT`n    bEnable : BOOL;`nEND_VAR`nVAR`n    nCount : INT;`n    bDone : BOOL;`nEND_VAR"
$newImpl    = New-Code 'impl.st'    "nCount := nCount + 2;`nbDone := nCount > 10;"
$newMethod  = New-Code 'method2.st' "METHOD Step : BOOL`nVAR_INPUT`n    dt : LREAL;`nEND_VAR`nStep := dt > 0.0;"
$paramsFile = New-Code 'params.json' '{"GVL_Param_Tc3_Module":{"bExample":"TRUE"}}'

Write-Host "`n=== Scaffolding baseline (automation backend) ===" -ForegroundColor Cyan
$base = Join-Path $Root 'base'
$scaffold = Invoke-Cli -VerbArgs @('create-project', 'Parity', $base, '--writer', 'automation')
if (-not $scaffold.Success) {
    Write-Host "create-project failed; is TcXaeShell running? $($scaffold.Raw)" -ForegroundColor Red
    exit 1
}

Copy-Item -LiteralPath $base -Destination (Join-Path $Root 'xae') -Recurse
Copy-Item -LiteralPath $base -Destination (Join-Path $Root 'xml') -Recurse

$reopen = Invoke-Cli -VerbArgs @('open-project', (Join-Path $Root 'xae\Parity.sln'), '--writer', 'automation')
if (-not $reopen.Success) {
    Write-Host "open-project on the xae clone failed: $($reopen.Raw)" -ForegroundColor Red
    exit 1
}

if (-not (Compare-Trees -Label 'baseline')) {
    Write-Host "Clones diverge before any verb ran; canonicaliser or clone problem." -ForegroundColor Red
    if (-not $Continue) { exit 1 }
}

# --- the verb sequence (smoke-writer order, minus XAE-only scaffolding) --------

$plc = 'Parity_Plc'
$sequence = @(
    @{ Label = 'add-folder';                      Args = @('add-folder', 'Drives', '--plc', $plc) },
    @{ Label = 'add-pou';                          Args = @('add-pou', 'FB_Parity', 'function_block', "@$fbCode", '--plc', $plc) },
    @{ Label = 'add-pou (interface)';              Args = @('add-pou', 'I_Parity', 'interface', 'INTERFACE I_Parity', '--plc', $plc) },
    @{ Label = 'add-gvl';                          Args = @('add-gvl', 'GVL_Parity', "@$gvlCode", '--plc', $plc) },
    @{ Label = 'add-dut';                          Args = @('add-dut', 'ST_Parity', "@$dutCode", '--kind', 'struct', '--plc', $plc) },
    @{ Label = 'add-method';                       Args = @('add-method', 'FB_Parity', 'Step', "@$methodCode", '--plc', $plc) },
    @{ Label = 'add-property';                     Args = @('add-property', 'FB_Parity', 'Value', 'INT', '--get', "@$getCode", '--set', "@$setCode", '--plc', $plc) },
    @{ Label = 'add-variable';                     Args = @('add-variable', 'FB_Parity', 'VAR_INPUT', 'bEnable : BOOL;', '--plc', $plc) },
    @{ Label = 'update-pou-declaration';           Args = @('update-pou-declaration', 'FB_Parity', "@$newDecl", '--plc', $plc) },
    @{ Label = 'update-pou-implementation';        Args = @('update-pou-implementation', 'FB_Parity', "@$newImpl", '--plc', $plc) },
    @{ Label = 'update-method-body';               Args = @('update-method-body', 'FB_Parity', 'Step', "@$newMethod", '--plc', $plc) },
    @{ Label = 'update-pou-declaration-patch';     Args = @('update-pou-declaration-patch', 'FB_Parity', 'bDone : BOOL;', 'bDone : BOOL; // done', '--plc', $plc) },
    @{ Label = 'update-pou-implementation-patch';  Args = @('update-pou-implementation-patch', 'FB_Parity', '+ 2;', '+ 3;', '--plc', $plc) },
    @{ Label = 'update-method-body-patch';         Args = @('update-method-body-patch', 'FB_Parity', 'Step', 'dt > 0.0', 'dt >= 0.0', '--plc', $plc) },
    @{ Label = 'add-library-placeholder';          Args = @('add-library-placeholder', 'Tc3_Module', 'Tc3_Module', '--distributor', 'Beckhoff Automation GmbH', '--plc', $plc) },
    @{ Label = 'set-placeholder-parameters';       Args = @('set-placeholder-parameters', 'Tc3_Module', "@$paramsFile", '--plc', $plc) },
    @{ Label = 'add-library-reference';            Args = @('add-library-reference', 'Tc2_Utilities', '--version', '*', '--distributor', 'Beckhoff Automation GmbH', '--plc', $plc) },
    @{ Label = 'delete-library-reference';         Args = @('delete-library-reference', 'Tc2_Utilities', '--version', '*', '--distributor', 'Beckhoff Automation GmbH', '--plc', $plc) },
    @{ Label = 'delete-placeholder';               Args = @('delete-placeholder', 'Tc3_Module', '--plc', $plc) },
    @{ Label = 'delete-variable';                  Args = @('delete-variable', 'FB_Parity', 'bEnable', '--plc', $plc) },
    @{ Label = 'delete-property';                  Args = @('delete-property', 'FB_Parity', 'Value', '--plc', $plc) },
    @{ Label = 'delete-method';                    Args = @('delete-method', 'FB_Parity', 'Step', '--plc', $plc) },
    @{ Label = 'delete-dut';                       Args = @('delete-dut', 'ST_Parity', '--plc', $plc) },
    @{ Label = 'delete-gvl';                       Args = @('delete-gvl', 'GVL_Parity', '--plc', $plc) },
    @{ Label = 'delete-pou (interface)';           Args = @('delete-pou', 'I_Parity', '--plc', $plc) },
    @{ Label = 'delete-pou';                       Args = @('delete-pou', 'FB_Parity', '--plc', $plc) },
    @{ Label = 'delete-folder';                    Args = @('delete-folder', 'Drives', '--plc', $plc) }
)

Write-Host "`n=== Verb-by-verb parity ===" -ForegroundColor Cyan
foreach ($step in $sequence) {
    $ok = Invoke-Pair -Label $step.Label -VerbArgs $step.Args
    if (-not $ok -and -not $Continue) { break }
}

if (-not $KeepScratch) {
    Write-Host "`nRemoving scratch at $Root ..." -ForegroundColor Cyan
    try {
        Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "  (could not remove; XAE may still hold the solution open) $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ("`n==== {0} in parity, {1} diverged ====" -f $script:pass, $script:fail) `
    -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) {
    Write-Host ("Diverged: {0}" -f ($script:diverged -join ', ')) -ForegroundColor Red
    exit 1
}
exit 0
