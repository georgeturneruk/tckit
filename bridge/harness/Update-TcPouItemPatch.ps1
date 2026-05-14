<#
.SYNOPSIS
    Replace one occurrence of a string in a POU item's combined source.

.DESCRIPTION
    Edit-style anchored replacement. Reads the item's combined declaration +
    implementation, counts occurrences of $OldString, fails on 0 or >1, then
    writes the replacement back via Set-TcItemSource. Mirror of Claude Code's
    own Edit semantics. See ADR-0003.

    Passing the POU name as $ItemName (or omitting $ItemName) targets the
    FB-level declaration + cyclic body, matching Update-TcPouItem.ps1.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER ItemName
    Name of the method / action / property to patch. Defaults to PouName
    (FB-level item).

.PARAMETER OldString
    Text to match. Must appear exactly once in the combined source.

.PARAMETER NewString
    Replacement text.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$ItemName    = '',
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

    $item = $pou
    if ($ItemName -and $ItemName -ne $PouName) {
        $item = Find-TcChild -Root $pou -Name $ItemName
        if ($null -eq $item) {
            return @{ success = $false; error = "Item '$ItemName' not found on POU '$PouName'." }
        }
    }

    $source = Get-TcItemSource -Item $item
    $combined = $source.code

    $count = ([regex]::Matches($combined, [regex]::Escape($OldString))).Count
    if ($count -eq 0) {
        return @{ success = $false; error = "OldString not found in '$PouName.$ItemName'." }
    }
    if ($count -gt 1) {
        return @{ success = $false; error = "OldString appears $count times in '$PouName.$ItemName'; anchor must be unique. Extend OldString with more surrounding context." }
    }

    $idx = $combined.IndexOf($OldString)
    $patched = $combined.Substring(0, $idx) + $NewString + $combined.Substring($idx + $OldString.Length)

    Set-TcItemSource -Item $item -Code $patched
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; item = $ItemName; plc = $plcName; replacements = 1 }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
