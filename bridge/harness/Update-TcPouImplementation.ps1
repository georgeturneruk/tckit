<#
.SYNOPSIS
    Replace the POU-level implementation block (ImplementationText) on a POU item.

.DESCRIPTION
    Writes only ImplementationText on the POU tree node; the declaration
    block and any nested methods/actions/properties are left untouched.
    Use Update-TcPouDeclaration.ps1 for the FB-level declaration and
    Update-TcMethodBody.ps1 for a named method/action/property.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the POU whose implementation should be replaced.

.PARAMETER Code
    New implementation source — ST statements only (no FUNCTION_BLOCK
    header, no VAR/END_VAR).
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
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

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    Invoke-WithComRetry { $pou.ImplementationText = $Code } | Out-Null
    Save-TcSolution -Dte $dte

    return @{ success = $true; details = @{ pou = $PouName; plc = $plcName; target = 'implementation' } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
