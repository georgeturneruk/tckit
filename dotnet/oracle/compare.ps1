<#
.SYNOPSIS
    Parity-oracle cross-check (ADR-0015). Runs a reader through the Python TcKit (the
    behavioural reference) and the C# TcKit, then reports semantic differences.

.DESCRIPTION
    Not a byte-for-byte gate. The C# rewrite is free to make deliberate, reviewed
    improvements to the surface; this harness surfaces *meaning* differences so
    intended changes read as expected and genuine translation drift (a missing POU,
    a mis-detected type) stands out. The xUnit tests are the verification gate.

    Flow: run both -> parse JSON -> canonicalise (sort object keys, preserve array
    order, mask GUIDs, lower-case path-valued fields) -> diff. Wired for the reader
    lane: get_structure / get_pou_interface / get_pou_declaration / get_pou_item /
    get_gvl / get_dut. Per-symbol readers prime the index with get_structure first.

.EXAMPLE
    pwsh dotnet/oracle/compare.ps1 -Fixture C:\tckit\tests\fixtures\sample_project
    pwsh dotnet/oracle/compare.ps1 -Tool get_pou_interface -Fixture <dir> -Name FB_Example
    pwsh dotnet/oracle/compare.ps1 -Tool get_pou_item -Fixture <dir> -Name FB_Example -Item Execute
#>
param(
    [ValidateSet('get_structure', 'get_pou_interface', 'get_pou_declaration', 'get_pou_item', 'get_gvl', 'get_dut')]
    [string]$Tool = 'get_structure',
    [Parameter(Mandatory)][string]$Fixture,
    [string]$Name = '',   # symbol name (POU / GVL / DUT) for per-symbol readers
    [string]$Item = '',   # item name for get_pou_item
    [string]$Plc = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$dotnet = if (Test-Path 'C:\dn8\dotnet.exe') { 'C:\dn8\dotnet.exe' } else { 'dotnet' }

# Keys whose values are filesystem paths: lower-cased before diffing so .resolve()
# vs GetFullPath casing never reads as a real difference.
$pathKeys = @('project_path', 'solution_path', 'plcproj_path', 'path')

# Build the canonical form as a string directly. We avoid ConvertTo-Json because it
# unrolls empty and single-element arrays (renders [] as null and [x] as x), which
# would manufacture false differences.
function Get-Canonical($node, [string]$key) {
    if ($null -eq $node) { return 'null' }
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        $parts = foreach ($name in ($node.PSObject.Properties.Name | Sort-Object)) {
            '"' + $name + '":' + (Get-Canonical $node.$name $name)
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($node -is [System.Collections.IList]) {
        $items = foreach ($item in $node) { Get-Canonical $item $key }
        return '[' + ($items -join ',') + ']'
    }
    if ($node -is [bool]) { return $node.ToString().ToLowerInvariant() }
    if ($node -is [string]) {
        $value = $node -replace '\{[0-9a-fA-F-]{36}\}', '{GUID}'
        if ($pathKeys -contains $key) { $value = $value.ToLowerInvariant() }
        return '"' + $value + '"'
    }
    return [string]$node  # numbers
}

function Format-Canonical([string]$json) {
    if ([string]::IsNullOrWhiteSpace($json)) { return '<empty>' }
    return Get-Canonical ($json | ConvertFrom-Json) ''
}

# Python call. Per-symbol readers prime the reader index with get_structure first
# (in the same process), mirroring how the C# CLI verbs prime before reading.
$plcKw = if ($Plc) { ", plc_name=r'$Plc'" } else { '' }
$pyCall = switch ($Tool) {
    'get_structure'       { "print(s.get_structure(r'$Fixture'$plcKw))" }
    'get_pou_interface'   { "s.get_structure(r'$Fixture'); print(s.get_pou_interface(r'$Name'$plcKw))" }
    'get_pou_declaration' { "s.get_structure(r'$Fixture'); print(s.get_pou_declaration(r'$Name'$plcKw))" }
    'get_pou_item'        { "s.get_structure(r'$Fixture'); print(s.get_pou_item(r'$Name', r'$Item'$plcKw))" }
    'get_gvl'             { "s.get_structure(r'$Fixture'); print(s.get_gvl(r'$Name'$plcKw))" }
    'get_dut'             { "s.get_structure(r'$Fixture'); print(s.get_dut(r'$Name'$plcKw))" }
}
Push-Location $repoRoot
try { $expected = python -c "import tckit.server as s; $pyCall" | Out-String } finally { Pop-Location }

# C# CLI call (shares the reader + serialiser with the MCP tools).
$verb = $Tool -replace '_', '-'
$cliArgs = @('run', '--project', "$repoRoot\dotnet\src\TcKit.Cli", '--no-build', '--', $verb, $Fixture)
if ($Name) { $cliArgs += $Name }
if ($Item) { $cliArgs += $Item }
if ($Plc) { $cliArgs += @('--plc', $Plc) }
$actual = (& $dotnet @cliArgs) | Out-String

# Compare canonical forms.
$label = "$Tool  $Fixture" + $(if ($Name) { "  $Name" } else { '' }) + $(if ($Item) { ".$Item" } else { '' }) + $(if ($Plc) { "  [$Plc]" } else { '' })
if ((Format-Canonical $expected) -eq (Format-Canonical $actual)) {
    Write-Host "PASS   $label" -ForegroundColor Green
} else {
    Write-Host "REVIEW $label" -ForegroundColor Yellow
    Write-Host '--- canonical reference (PY) ---'
    Write-Host (Format-Canonical $expected)
    Write-Host '--- canonical candidate (C#) ---'
    Write-Host (Format-Canonical $actual)
}
