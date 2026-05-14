<#
.SYNOPSIS
    Add a library reference to a consumer PLC project.

.DESCRIPTION
    Wraps ITcPlcLibraryManager.AddLibrary(name, version, distributor). The
    library manager hangs off the PLC project's References tree node at
    'TIPC^<plc>^<plc> Project^References'; PowerShell COM dispatch resolves
    AddLibrary on the tree item without an explicit cast.

    The referenced library must already be installed. For libraries
    produced from an in-sln PLC project, call Save-TcPlcAsLibrary.ps1 with
    -Install $true first.

    See ADR-0009.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Consumer PLC project receiving the reference. Optional if exactly one
    PLC project exists in the sln. Falls back to PLC_PROJECT_NAME.

.PARAMETER LibraryName
    Library name as installed (typically matches the source PLC project's
    name).

.PARAMETER Version
    '*' (default) means latest available, or a specific version like
    '1.0.0.0'.

.PARAMETER Distributor
    Library distributor / company string. Defaults to 'Tc3 Project', the
    conventional value for libraries produced from a PLC project via
    SaveAsLibrary; override if the project's company info differs.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$LibraryName,
    [string]$Version     = '*',
    [string]$Distributor = 'Tc3 Project',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $LibraryName) { return @{ success = $false; error = 'LibraryName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    # References node is the library manager via COM dispatch.
    $libManager = $sm.LookupTreeItem("TIPC^$plc^$plc Project^References")
    $libManager.AddLibrary($LibraryName, $Version, $Distributor) | Out-Null
    # AddLibrary mutates only in-memory state; persist to .plcproj so the
    # change survives a re-open / git-reset cycle. See Save-TcSolution
    # for the cross-cutting rationale.
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            consumer_plc = $plc
            library      = $LibraryName
            version      = $Version
            distributor  = $Distributor
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
