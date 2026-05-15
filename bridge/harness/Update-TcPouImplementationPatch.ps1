<#
.SYNOPSIS
    Replace one occurrence of a string in a POU's implementation block.

.DESCRIPTION
    Edit-style anchored replacement. Reads the POU's ImplementationText,
    counts occurrences of $OldString, fails on 0 or >1, then writes the
    patched implementation back. Mirror of Claude Code's own Edit
    semantics. See ADR-0003.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the POU whose implementation should be patched.

.PARAMETER OldString
    Text to match. Must appear exactly once in the implementation.

.PARAMETER NewString
    Replacement text.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
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
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PouName)     { return @{ success = $false; error = 'PouName required.' } }
    if (-not $OldString)   { return @{ success = $false; error = 'OldString required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $implementation = ''
    try { $implementation = [string]$pou.ImplementationText } catch { $implementation = '' }

    $count = ([regex]::Matches($implementation, [regex]::Escape($OldString))).Count
    if ($count -eq 0) {
        return @{ success = $false; error = "OldString not found in '$PouName' implementation." }
    }
    if ($count -gt 1) {
        return @{ success = $false; error = "OldString appears $count times in '$PouName' implementation; anchor must be unique. Extend OldString with more surrounding context." }
    }

    $idx = $implementation.IndexOf($OldString)
    $patched = $implementation.Substring(0, $idx) + $NewString + $implementation.Substring($idx + $OldString.Length)

    Invoke-WithComRetry { $pou.ImplementationText = $patched } | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; plc = $plcName; target = 'implementation'; replacements = 1 }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
