<#
.SYNOPSIS
    Deploy a built TwinCAT configuration to a target runtime via COM.

.DESCRIPTION
    Sets the target NetId on the chosen TwinCAT project's system manager,
    enables BootProjectAutostart + GenerateBootProject on the PLC tree
    item, then calls ActivateConfiguration(). In a multi-tsproj sln (one
    .tsproj per PLC, as produced by Add-TcPlcProject) you must pass
    -PlcName so the right TwinCAT project is targeted; in a single-tsproj
    sln the PlcName is optional.

    Without the autostart step, ActivateConfiguration only puts TwinCAT
    into Run mode and downloads the bootapp — the PLC application stays
    loaded but stopped until a manual Login + Start. A stopped PLC
    doesn't serve the symbol table on its ADS port, so subsequent
    /tcunit-run polls see "Target doesn't provide symbolic information"
    and time out. Enabling autostart via ITcPlcProject is what the IDE's
    "Activate Configuration" dialog does when you tick "Autostart boot
    project"; see
    https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html
    for the canonical example.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER TargetAmsId
    AMS Net ID of the target (e.g. 192.168.1.100.1.1). Falls back to TARGET_AMS_ID env var.

.PARAMETER PlcName
    Name of the PLC whose containing TwinCAT project should be deployed.
    Optional; falls back to PLC_PROJECT_NAME env var, then to the only
    PLC in the solution if there's exactly one.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.PARAMETER XaeMode
    'attach' (default) or 'headless'.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null

    $resolvedPlc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $resolvedPlc

    # Set target NetId before activation so the bootapp lands on the
    # right runtime.
    try {
        $sm.SetTargetNetId($TargetAmsId)
    } catch {
        return @{ success = $false; error = "SetTargetNetId('$TargetAmsId') failed: $($_.Exception.Message)" }
    }

    # Enable autostart on the PLC project so the runtime actually runs
    # the application (not just loads it) when the system reaches Run
    # mode. GenerateBootProject(true) writes the boot artefacts the
    # runtime picks up on startup.
    #
    # ITcPlcProject (which exposes BootProjectAutostart) lives at the
    # SM-level PLC instance — TIPC^<plc> — NOT at the IDE-level PLC
    # project node TIPC^<plc>^<plc> Project (that one exposes
    # ITcPlcIECProject for source authoring). See infosys
    # "Accessing, creating and handling PLC projects".
    try {
        $plcSmNode = $sm.LookupTreeItem("TIPC^$resolvedPlc")
        $plcSmNode.BootProjectAutostart = $true
        $plcSmNode.GenerateBootProject($true)
    } catch {
        return @{
            success = $false
            error   = "Enabling autostart on '$resolvedPlc' failed: $($_.Exception.Message)"
        }
    }

    try {
        $sm.ActivateConfiguration()
    } catch {
        return @{ success = $false; error = "ActivateConfiguration failed: $($_.Exception.Message)" }
    }

    return @{ success = $true; details = @{ target = $TargetAmsId; plc = $resolvedPlc; autostart = $true } }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
