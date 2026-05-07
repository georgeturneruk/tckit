<#
.SYNOPSIS
    Overwrite the source of an existing method / action / property — or the
    body of a POU itself when ItemName matches the POU name.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back to
    PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER ItemName
    Name of the method / action / property to overwrite. Pass the POU name
    (or omit) to target the POU's own declaration + implementation.

.PARAMETER Code
    Combined declaration + implementation. Splitter rules in Add-TcPou.ps1.

.PARAMETER Declaration
    Optional explicit declaration (overrides the split of $Code).

.PARAMETER Implementation
    Optional explicit implementation.
#>
param(
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH,
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$ItemName       = '',
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
    if (-not ($Code -or $Declaration -or $Implementation)) {
        return @{ success = $false; error = 'Code (or Declaration/Implementation) required.' }
    }

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

    if ($Declaration -or $Implementation) {
        Set-TcItemSource -Item $item -Declaration $Declaration -Implementation $Implementation
    } else {
        Set-TcItemSource -Item $item -Code $Code
    }

    return @{ success = $true; details = @{ pou = $PouName; item = $ItemName; plc = $plcName } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
