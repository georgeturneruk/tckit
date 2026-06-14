<#
.SYNOPSIS
    Replace one occurrence of a string in a POU's declaration block.

.DESCRIPTION
    Edit-style anchored replacement. Reads the POU's DeclarationText,
    counts occurrences of $OldString, fails on 0 or >1, then writes the
    patched declaration back. Mirror of Claude Code's own Edit semantics.
    See ADR-0003.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the POU whose declaration should be patched.

.PARAMETER OldString
    Text to match. Must appear exactly once in the declaration.

.PARAMETER NewString
    Replacement text.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$OldString   = '',
    [string]$NewString   = '',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PouName)     { return @{ success = $false; error = 'PouName required.' } }
    if (-not $OldString)   { return @{ success = $false; error = 'OldString required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $declaration = ''
    try { $declaration = [string]$pou.DeclarationText } catch { $declaration = '' }

    $count = ([regex]::Matches($declaration, [regex]::Escape($OldString))).Count
    if ($count -eq 0) {
        return @{ success = $false; error = "OldString not found in '$PouName' declaration." }
    }
    if ($count -gt 1) {
        return @{ success = $false; error = "OldString appears $count times in '$PouName' declaration; anchor must be unique. Extend OldString with more surrounding context." }
    }

    $idx = $declaration.IndexOf($OldString)
    $patched = $declaration.Substring(0, $idx) + $NewString + $declaration.Substring($idx + $OldString.Length)

    Invoke-WithComRetry { $pou.DeclarationText = $patched } | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; plc = $plcName; target = 'declaration'; replacements = 1 }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
