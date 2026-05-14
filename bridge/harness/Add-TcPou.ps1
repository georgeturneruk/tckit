<#
.SYNOPSIS
    Add a new POU under a PLC project's POUs folder, then write its source.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project (under TIPC). Optional if exactly one PLC project
    is present. Falls back to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the new POU (e.g. FB_MyMotor).

.PARAMETER PouType
    One of: function_block, function, program, interface.

.PARAMETER Code
    Combined ST source — declaration block (ending with the last END_VAR) plus
    implementation body. Passing only a declaration is valid; the body will be
    left empty.

.PARAMETER Declaration
    Optional. If provided, used directly (overrides the split of $Code).

.PARAMETER Implementation
    Optional. If provided, used directly.
#>
param(
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH,
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$PouType,
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
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }
    if (-not $PouType)     { return @{ success = $false; error = 'PouType required.' } }

    $kind = Get-TcKind -Type $PouType

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $sm = Get-TcSysManager -Dte $dte
    $plcName = Resolve-TcPlcName -SysManager $sm -Explicit $PlcName

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    $newItem = $pous.CreateChild($Name, $kind, $null, $null)

    if ($Declaration -or $Implementation) {
        Set-TcItemSource -Item $newItem -Declaration $Declaration -Implementation $Implementation
    } elseif ($Code) {
        Set-TcItemSource -Item $newItem -Code $Code
    }
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; kind = $kind; plc = $plcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
