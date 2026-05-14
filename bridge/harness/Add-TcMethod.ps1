<#
.SYNOPSIS
    Add a new method (or action / property) to an existing POU.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER MethodName
    Name of the new item.

.PARAMETER ItemType
    Kind of item to add. One of: method (default), action, property.

.PARAMETER Code
    Combined declaration + implementation. See Add-TcPou.ps1 for shape.

.PARAMETER Declaration
    Optional explicit declaration (overrides the split of $Code).

.PARAMETER Implementation
    Optional explicit implementation.
#>
param(
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH,
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$MethodName,
    [ValidateSet('method', 'action', 'property')][string]$ItemType = 'method',
    [string]$Code           = '',
    [string]$Declaration    = '',
    [string]$Implementation = '',
    [string]$ComVersion     = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode        = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PouName)     { return @{ success = $false; error = 'PouName required.' } }
    if (-not $MethodName)  { return @{ success = $false; error = 'MethodName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte
    $plcName = Resolve-TcPlcName -SysManager $sm -Explicit $PlcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $kind = Get-TcKind -Type $ItemType
    $newItem = $pou.CreateChild($MethodName, $kind, $null, $null)

    if ($Declaration -or $Implementation) {
        Set-TcItemSource -Item $newItem -Declaration $Declaration -Implementation $Implementation
    } elseif ($Code) {
        Set-TcItemSource -Item $newItem -Code $Code
    }
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; method = $MethodName; kind = $kind; plc = $plcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
