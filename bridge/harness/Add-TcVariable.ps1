<#
.SYNOPSIS
    Add one variable declaration to a named scope block on a POU or method.

.DESCRIPTION
    Thin convenience over the patch primitive: reads the target item's
    declaration, finds the requested scope block (VAR_INPUT, VAR_OUTPUT,
    VAR_IN_OUT, VAR, VAR_PERSISTENT, VAR_TEMP, VAR CONSTANT), and inserts
    the new declaration line before its END_VAR. If the scope block does
    not exist, appends a fresh one to the end of the declaration. See
    ADR-0003.

    Passing $ItemName targets a method's local VAR block; omitting it
    targets the FB-level declaration.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls
    back to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the containing POU.

.PARAMETER Scope
    Scope block keyword: VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR,
    VAR_PERSISTENT, VAR_TEMP, or 'VAR CONSTANT'.

.PARAMETER Declaration
    Single variable declaration, e.g. 'bNewParam : BOOL;'.

.PARAMETER ItemName
    Method to add the local variable to. Default: FB-level declaration.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$Scope       = '',
    [string]$Declaration = '',
    [string]$ItemName    = '',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

function Get-ScopePattern {
    param([string]$ScopeName)
    $key = $ScopeName.Trim().ToUpperInvariant()
    switch ($key) {
        'VAR_INPUT'      { return '^VAR_INPUT$' }
        'VAR_OUTPUT'     { return '^VAR_OUTPUT$' }
        'VAR_IN_OUT'     { return '^VAR_IN_OUT$' }
        'VAR_TEMP'       { return '^VAR_TEMP$' }
        'VAR_PERSISTENT' { return '^VAR_PERSISTENT$' }
        'VAR'            { return '^VAR$' }
        'VAR CONSTANT'   { return '^VAR\s+CONSTANT$' }
        default          { throw "Unknown scope '$ScopeName'. Use VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR, VAR_PERSISTENT, VAR_TEMP, or 'VAR CONSTANT'." }
    }
}

function Add-VariableToDeclaration {
    param(
        [Parameter(Mandatory)][string]$DeclarationText,
        [Parameter(Mandatory)][string]$ScopeName,
        [Parameter(Mandatory)][string]$VariableLine
    )

    $pattern = Get-ScopePattern -ScopeName $ScopeName
    $insert = "    $VariableLine"

    $normalised = $DeclarationText -replace "`r`n", "`n"
    $lines = [System.Collections.Generic.List[string]]@($normalised -split "`n")

    $headerIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed -match $pattern) {
            $headerIdx = $i
            break
        }
    }

    if ($headerIdx -ge 0) {
        $endIdx = -1
        for ($j = $headerIdx + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -eq 'END_VAR') {
                $endIdx = $j
                break
            }
        }
        if ($endIdx -lt 0) {
            throw "Scope '$ScopeName' opened at line $($headerIdx + 1) has no matching END_VAR."
        }
        $lines.Insert($endIdx, $insert)
    }
    else {
        # No matching block; append a fresh one to the end of the declaration.
        # Trim any trailing blank lines so the new block sits cleanly.
        while ($lines.Count -gt 0 -and ($lines[$lines.Count - 1].Trim() -eq '')) {
            $lines.RemoveAt($lines.Count - 1)
        }
        $headerText = $ScopeName.Trim().ToUpperInvariant()
        $lines.Add($headerText)
        $lines.Add($insert)
        $lines.Add('END_VAR')
    }

    return ($lines -join "`n")
}

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PouName)     { return @{ success = $false; error = 'PouName required.' } }
    if (-not $Scope)       { return @{ success = $false; error = 'Scope required.' } }
    if (-not $Declaration) { return @{ success = $false; error = 'Declaration required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte
    $plcName = Resolve-TcPlcName -SysManager $sm -Explicit $PlcName

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
    $newDecl = Add-VariableToDeclaration `
        -DeclarationText $source.declaration `
        -ScopeName $Scope `
        -VariableLine $Declaration

    Set-TcItemSource -Item $item -Declaration $newDecl -Implementation $source.implementation

    return @{
        success = $true
        details = @{
            pou = $PouName
            item = $ItemName
            plc = $plcName
            scope = $Scope.Trim().ToUpperInvariant()
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
