<#
.SYNOPSIS
    Add a library placeholder reference to a consumer PLC project.

.DESCRIPTION
    Wraps ITcPlcLibraryManager.AddPlaceholder(name, defaultLib, defaultVersion,
    defaultDistributor). The library manager hangs off the PLC project's
    References tree node at 'TIPC^<plc>^<plc> Project^References';
    PowerShell COM dispatch resolves AddPlaceholder on the tree item without
    an explicit cast.

    The placeholder's default-resolution library must already be installed.
    For libraries produced from an in-sln PLC project, call
    Save-TcPlcAsLibrary.ps1 with -Install $true first. System placeholders
    (Tc2_System, Tc2_Standard, Tc3_Module, etc.) resolve against vendor
    libraries shipped with the TwinCAT install.

    Produces a <PlaceholderReference> entry in the consumer .plcproj rather
    than the <LibraryReference> produced by Add-TcLibraryReference.ps1. See
    ADR-0009.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    Consumer PLC project receiving the reference. Optional if exactly one
    PLC project exists in the sln. Falls back to PLC_PROJECT_NAME.

.PARAMETER PlaceholderName
    Placeholder name (the value that lands in <PlaceholderReference Include=>).
    Typically matches DefaultLibrary but can differ.

.PARAMETER DefaultLibrary
    Library the placeholder resolves to by default.

.PARAMETER Version
    Default library version. '*' (default) means latest available.

.PARAMETER Distributor
    Default library distributor / company string. Empty default matches the
    documented API default; pass explicitly for non-system libraries (e.g.
    'www.tcunit.org' for TcUnit, 'Beckhoff Automation GmbH' for Tc2/Tc3
    libraries).
#>
param(
    [string]$ProjectPath     = $env:PLC_PROJECT_PATH,
    [string]$PlcName         = $env:PLC_PROJECT_NAME,
    [string]$PlaceholderName,
    [string]$DefaultLibrary,
    [string]$Version         = '*',
    [string]$Distributor     = '',
    [string]$ComVersion      = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode         = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath)     { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PlaceholderName) { return @{ success = $false; error = 'PlaceholderName required.' } }
    if (-not $DefaultLibrary)  { return @{ success = $false; error = 'DefaultLibrary required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    # References node is the library manager via COM dispatch.
    $libManager = $sm.LookupTreeItem("TIPC^$plc^$plc Project^References")
    $libManager.AddPlaceholder($PlaceholderName, $DefaultLibrary, $Version, $Distributor) | Out-Null
    # AddPlaceholder mutates only in-memory state; persist to .plcproj so the
    # change survives a re-open / git-reset cycle. See Save-TcSolution.
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            consumer_plc     = $plc
            placeholder      = $PlaceholderName
            default_library  = $DefaultLibrary
            version          = $Version
            distributor      = $Distributor
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
