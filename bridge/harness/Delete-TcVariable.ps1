<#
.SYNOPSIS
    Remove a single variable declaration from a POU's or method's
    declaration block.

.DESCRIPTION
    Mirror of Add-TcVariable.ps1: reads the target item's declaration,
    locates the named variable's line inside one of the VAR / VAR_INPUT /
    VAR_OUTPUT / VAR_IN_OUT / VAR_TEMP / VAR_PERSISTENT / VAR CONSTANT
    blocks, removes it, and writes the declaration back via
    ITcPlcDeclaration.DeclarationText.

    Multi-name declarations (e.g. ``bA, bB, bC : BOOL;``) are refused with
    a clear error pointing at update_pou_declaration_patch — splitting one
    variable out of a comma list isn't reversible without parsing the
    type, default value, and trailing comment.

    Variable lines that span continuations are also refused; the
    expected shape is one variable per line.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls
    back to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the containing POU.

.PARAMETER VariableName
    Name of the variable to remove.

.PARAMETER ItemName
    Method to remove the local variable from. Default: FB-level
    declaration.
#>
param(
    [string]$ProjectPath  = $env:PLC_PROJECT_PATH,
    [string]$PlcName      = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$VariableName,
    [string]$ItemName     = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

function Remove-VariableFromDeclaration {
    param(
        [Parameter(Mandatory)][string]$DeclarationText,
        [Parameter(Mandatory)][string]$VariableName
    )

    $normalised = $DeclarationText -replace "`r`n", "`n"
    $lines = $normalised -split "`n"

    $matchIndices = @()
    # The variable's declaration starts with its identifier, optionally
    # followed by an AT location, then a colon. Anything followed by a
    # comma before the colon means a multi-name list; refuse that.
    $singleNamePattern = "^\s*$([regex]::Escape($VariableName))\b\s*(AT\s+[^:]+)?\s*:"
    $multiNamePattern  = "^\s*$([regex]::Escape($VariableName))\b\s*,"
    $listPrefixPattern = "^\s*[A-Za-z_][A-Za-z0-9_]*(\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*\s*,\s*$([regex]::Escape($VariableName))\b\s*[,:]"

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match $multiNamePattern) {
            throw "Variable '$VariableName' is in a multi-name declaration (line $($i + 1)). Use update_pou_declaration_patch for partial edits."
        }
        if ($line -match $listPrefixPattern) {
            throw "Variable '$VariableName' is part of a multi-name list (line $($i + 1)). Use update_pou_declaration_patch for partial edits."
        }
        if ($line -match $singleNamePattern) {
            # Refuse line continuation: a trailing un-terminated paren or a
            # bare line that doesn't reach the closing ';' on the same line.
            if ($line -notmatch ';') {
                throw "Variable '$VariableName' line $($i + 1) doesn't terminate with ';' on the same line. Use update_pou_declaration_patch."
            }
            $matchIndices += $i
        }
    }

    if ($matchIndices.Count -eq 0) {
        throw "Variable '$VariableName' not found in declaration."
    }
    if ($matchIndices.Count -gt 1) {
        $lineNumbers = ($matchIndices | ForEach-Object { $_ + 1 }) -join ', '
        throw "Variable '$VariableName' appears on multiple lines ($lineNumbers); cannot disambiguate."
    }

    $removeIdx = $matchIndices[0]
    $kept = New-Object 'System.Collections.Generic.List[string]'
    for ($j = 0; $j -lt $lines.Length; $j++) {
        if ($j -ne $removeIdx) { $kept.Add($lines[$j]) }
    }
    return ($kept -join "`n")
}

try {
    if (-not $ProjectPath)  { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PouName)      { return @{ success = $false; error = 'PouName required.' } }
    if (-not $VariableName) { return @{ success = $false; error = 'VariableName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $item = $pou
    if ($ItemName -and $ItemName -ne $PouName) {
        $item = Find-TcChild -Root $pou -Name $ItemName
        if ($null -eq $item) {
            return @{ success = $false; error = "Item '$ItemName' not found on POU '$PouName'." }
        }
    }

    $source = Get-TcItemSource -Item $item
    $newDecl = Remove-VariableFromDeclaration -DeclarationText $source.declaration -VariableName $VariableName

    Set-TcItemSource -Item $item -Declaration $newDecl -Implementation $source.implementation
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; item = $ItemName; variable = $VariableName; plc = $plcName }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
