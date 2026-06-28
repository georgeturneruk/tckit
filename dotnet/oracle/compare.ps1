<#
.SYNOPSIS
    Parity-oracle harness skeleton (ADR-0015). Diffs a C# tool's output against the
    Python TcKit golden master for the same operation.

.DESCRIPTION
    Skeleton only: the two server invocations are stubbed until the C# tools exist.
    The flow it locks in is run-both, normalise, diff. See oracle/README.md.
#>
param(
    [Parameter(Mandatory)][string]$Tool,     # e.g. get_structure
    [string]$ArgumentsJson = '{}',
    [string]$Fixture                         # path to the fixture .sln / .plcproj
)

# Mask volatile fields so they do not produce false diffs.
function Format-Normalised([string]$json) {
    if ([string]::IsNullOrEmpty($json)) { return '' }
    return ($json -replace '\{[0-9a-fA-F-]{36}\}', '{GUID}')
}

# 1. Golden master: run $Tool on the Python TcKit.
# TODO: invoke the Python tckit MCP/CLI with $Tool + $ArgumentsJson, capture JSON.
$expected = $null

# 2. Candidate: run $Tool on the C# TcKit.Server over stdio.
# TODO: invoke TcKit.Server with the same tool + args, capture JSON.
$actual = $null

# 3. Compare.
if ((Format-Normalised $expected) -eq (Format-Normalised $actual)) {
    Write-Host "PASS  $Tool"
} else {
    Write-Host "DIFF  $Tool"
    # TODO: emit a structured line/field diff to aid the fix.
}
