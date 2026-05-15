<#
.SYNOPSIS
    Replace one occurrence of a string in a method's combined source.

.DESCRIPTION
    Edit-style anchored replacement on a method, action, or property.
    Reads the item's combined declaration + implementation (via
    Get-TcItemSource), counts occurrences of $OldString, fails on 0 or
    >1, then writes the patched source back via Set-TcItemSource (which
    re-splits at END_VAR or the method header). Mirror of Claude Code's
    own Edit semantics. See ADR-0003.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER MethodName
    Name of the method, action, or property to patch.

.PARAMETER OldString
    Text to match. Must appear exactly once in the combined source.

.PARAMETER NewString
    Replacement text.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$MethodName,
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
    if (-not $MethodName)  { return @{ success = $false; error = 'MethodName required.' } }
    if (-not $OldString)   { return @{ success = $false; error = 'OldString required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $item = Find-TcChild -Root $pou -Name $MethodName
    if ($null -eq $item) {
        return @{ success = $false; error = "Item '$MethodName' not found on POU '$PouName'." }
    }

    $source = Get-TcItemSource -Item $item
    $combined = $source.code

    $count = ([regex]::Matches($combined, [regex]::Escape($OldString))).Count
    if ($count -eq 0) {
        return @{ success = $false; error = "OldString not found in '$PouName.$MethodName'." }
    }
    if ($count -gt 1) {
        return @{ success = $false; error = "OldString appears $count times in '$PouName.$MethodName'; anchor must be unique. Extend OldString with more surrounding context." }
    }

    $idx = $combined.IndexOf($OldString)
    $patched = $combined.Substring(0, $idx) + $NewString + $combined.Substring($idx + $OldString.Length)

    Set-TcItemSource -Item $item -Code $patched
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; method = $MethodName; plc = $plcName; replacements = 1 }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
