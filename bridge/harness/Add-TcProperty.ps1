<#
.SYNOPSIS
    Add a new property (with Get, Set, or both accessors) to an existing POU.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER PropertyName
    Name of the new property.

.PARAMETER ReturnType
    TwinCAT type the property exposes (e.g. LREAL, BOOL, E_MyEnum). Written
    to the property parent's declaration as ``PROPERTY <name> : <type>``.

.PARAMETER GetterCode
    Optional body of the Get accessor. May include a local VAR block. The
    bridge splits at the last END_VAR (Split-TcCode), or treats the whole
    string as the implementation when no VAR block is present. Empty string
    or absent: no Get accessor is created.

.PARAMETER SetterCode
    Optional body of the Set accessor. Same shape as GetterCode. Empty
    string or absent: no Set accessor is created.

    At least one of GetterCode or SetterCode must be supplied.
#>
param(
    [string]$ProjectPath  = $env:PLC_PROJECT_PATH,
    [string]$PlcName      = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$PropertyName,
    [string]$ReturnType,
    [string]$GetterCode   = '',
    [string]$SetterCode   = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath)    { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PouName)        { return @{ success = $false; error = 'PouName required.' } }
    if (-not $PropertyName)   { return @{ success = $false; error = 'PropertyName required.' } }
    if (-not $ReturnType)     { return @{ success = $false; error = 'ReturnType required.' } }
    if (-not $GetterCode -and -not $SetterCode) {
        return @{
            success = $false
            error   = 'add_property requires at least one of GetterCode or SetterCode.'
        }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $plcProj -Name $PouName
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $kindProperty = Get-TcKind -Type 'property'
    $kindGet      = Get-TcKind -Type 'property_get'
    $kindSet      = Get-TcKind -Type 'property_set'

    $newProperty = $pou.CreateChild($PropertyName, $kindProperty, $null, $null)
    Set-TcItemSource -Item $newProperty -Declaration "PROPERTY $PropertyName : $ReturnType"

    $accessors = @()

    if ($GetterCode) {
        $getItem = $newProperty.CreateChild('Get', $kindGet, $null, $null)
        Set-TcItemSource -Item $getItem -Code $GetterCode
        $accessors += 'Get'
    }

    if ($SetterCode) {
        $setItem = $newProperty.CreateChild('Set', $kindSet, $null, $null)
        Set-TcItemSource -Item $setItem -Code $SetterCode
        $accessors += 'Set'
    }

    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            pou         = $PouName
            property    = $PropertyName
            return_type = $ReturnType
            accessors   = $accessors
            plc         = $plcName
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
