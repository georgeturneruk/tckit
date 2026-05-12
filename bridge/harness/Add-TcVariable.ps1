<#
.SYNOPSIS
    Add one variable declaration to a named scope block on a POU or method.

.DESCRIPTION
    Thin convenience over the patch primitive: reads the target item's
    declaration, finds the requested scope block (VAR_INPUT, VAR_OUTPUT,
    VAR_IN_OUT, VAR, VAR_PERSISTENT, VAR_TEMP, VAR CONSTANT), and inserts
    the new declaration line before its END_VAR. If the scope block does
    not exist, a fresh block is created at the conventional position in
    the declaration (order: VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR,
    VAR CONSTANT, VAR_PERSISTENT, VAR_TEMP). See ADR-0003.

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

function Get-ScopeRank {
    param([string]$ScopeName)
    switch ($ScopeName.Trim().ToUpperInvariant()) {
        'VAR_INPUT'      { return 1 }
        'VAR_OUTPUT'     { return 2 }
        'VAR_IN_OUT'     { return 3 }
        'VAR'            { return 4 }
        'VAR CONSTANT'   { return 5 }
        'VAR_PERSISTENT' { return 6 }
        'VAR_TEMP'       { return 7 }
        default          { return 99 }
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
        # No matching block: create one at the conventional position.
        # Scan existing scope blocks inline (a separate function broke
        # parameter binding when passed a generic list in 5.1).
        $headerText = $ScopeName.Trim().ToUpperInvariant()
        $newRank = Get-ScopeRank -ScopeName $ScopeName
        $headerRegex = '^(VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_TEMP|VAR_PERSISTENT|VAR\s+CONSTANT|VAR)(\s|$)'

        $insertAt = -1
        $k = 0
        while ($k -lt $lines.Count) {
            $kTrimmed = $lines[$k].Trim()
            if ($kTrimmed -match $headerRegex) {
                $typeRaw = $matches[1] -replace '\s+', ' '
                $existingType = $typeRaw.ToUpperInvariant()
                $existingRank = Get-ScopeRank -ScopeName $existingType
                if ($existingRank -gt $newRank) {
                    $insertAt = $k
                    break
                }
                # Skip past this block's END_VAR so we don't re-match its body.
                $kEnd = -1
                for ($m = $k + 1; $m -lt $lines.Count; $m++) {
                    if ($lines[$m].Trim() -eq 'END_VAR') { $kEnd = $m; break }
                }
                if ($kEnd -ge 0) { $k = $kEnd + 1; continue }
            }
            $k++
        }

        if ($insertAt -lt 0) {
            # New block ranks at or after every existing block. Append at the
            # end of the declaration, trimming trailing blank lines so the new
            # block sits cleanly.
            while ($lines.Count -gt 0 -and ($lines[$lines.Count - 1].Trim() -eq '')) {
                $lines.RemoveAt($lines.Count - 1)
            }
            $insertAt = $lines.Count
        }

        $lines.InsertRange($insertAt, [string[]]@($headerText, $insert, 'END_VAR'))
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
