<#
.SYNOPSIS
    Add a new Global Variable List (GVL) under a PLC project's POUs folder,
    then write its declaration text.

.DESCRIPTION
    GVLs are tree items in their own right (kind 615 — see Get-TcKind in
    _TcDte.psm1) and aren't POUs. Add-TcPou.ps1 used to accept
    PouType: "gvl" as a punch-through, but that conflated two different
    concepts; this dedicated path is the writer-port-supported route.
    The /pou route now rejects PouType: "gvl" and points callers here.

    GVLs only carry a declaration block — there's no implementation
    body — so we set DeclarationText only. Set-TcItemSource skips the
    empty-string ImplementationText assignment for GVLs to keep round-
    trips clean.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project (under TIPC). Optional if exactly one PLC
    project is present. Falls back to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the new GVL (e.g. GVL_Settings).

.PARAMETER Code
    Full ST source text (VAR_GLOBAL ... END_VAR).
#>
param(
    [string]$ProjectPath  = '',
    [string]$PlcName      = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$Code         = '',
    [string]$ParentFolder = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $kind = Get-TcKind -Type 'gvl'

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    $parent = Resolve-TcFolderPath -Root $pous -Path $ParentFolder
    $newItem = $parent.CreateChild($Name, $kind, $null, $null)

    if ($Code) {
        Set-TcItemSource -Item $newItem -Declaration $Code
    }
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; kind = $kind; plc = $plcName; parent_folder = $ParentFolder }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
