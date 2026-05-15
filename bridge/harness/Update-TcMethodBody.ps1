<#
.SYNOPSIS
    Replace the full body of a method, action, or property item.

.DESCRIPTION
    `Code` is the combined declaration + implementation for the named item;
    Split-TcCode separates them at the last END_VAR (or the last METHOD /
    ACTION / PROPERTY header line when no VAR block is present) and
    DeclarationText / ImplementationText are written separately.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER MethodName
    Name of the method, action, or property to overwrite.

.PARAMETER Code
    Combined ST source for the item. Splitter rules in _TcDte.psm1
    (Split-TcCode).
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$MethodName,
    [string]$Code        = '',
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
    if (-not $Code)        { return @{ success = $false; error = 'Code required.' } }

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

    Set-TcItemSource -Item $item -Code $Code
    Save-TcSolution -Dte $dte

    return @{ success = $true; details = @{ pou = $PouName; method = $MethodName; plc = $plcName } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
