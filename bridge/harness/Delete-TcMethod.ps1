<#
.SYNOPSIS
    Delete a method (or action) from a POU.

.DESCRIPTION
    Locates the POU under TIPC^<plc>^<plc> Project^POUs, then calls
    DeleteChild on it with the method name. Works for plain methods,
    interface methods, and actions; the tree item's display name is
    the only thing DeleteChild keys off.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the containing POU.

.PARAMETER MethodName
    Name of the method (or action) to delete.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$MethodName,
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

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $pous -Name $PouName
    if ($null -eq $pou -or $pou.Name -eq $pous.Name) {
        return @{ success = $false; error = "POU '$PouName' not found under POUs of '$plcName'." }
    }

    $method = Find-TcChild -Root $pou -Name $MethodName
    if ($null -eq $method -or $method.Name -eq $pou.Name) {
        return @{ success = $false; error = "Method '$MethodName' not found under POU '$PouName'." }
    }

    $pou.DeleteChild($MethodName)
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; method = $MethodName; plc = $plcName }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
