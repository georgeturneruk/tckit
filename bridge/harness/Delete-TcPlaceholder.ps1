<#
.SYNOPSIS
    Remove a library placeholder reference from a consumer PLC project.

.DESCRIPTION
    Wraps the single-argument form of ITcPlcLibraryManager.RemoveReference,
    which targets placeholders specifically (the 3-arg form keys off library
    identity instead). See https://infosys.beckhoff.com/content/1033/
    tc3_automationinterface/242888843.html and Beckhoff's
    ManagePlcLibraries.cs for the pattern.

    Whether RemoveReference also strips an orphan <Parameters> block from
    the consumer .plcproj is undocumented; the bench will verify. If it
    turns out the parameters survive, the Beckhoff-blessed escape hatch is
    ITcPlcIECProject::ConsumeXml('<RemoveReferences><PlaceholderReference>
    <Name>X</Name></PlaceholderReference></RemoveReferences>') on the PLC
    project node, which we leave commented in place for the day we need it.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Consumer PLC project. Optional if exactly one PLC project is present.
    Falls back to PLC_PROJECT_NAME.

.PARAMETER PlaceholderName
    Placeholder reference name (the Include= attribute of the
    <PlaceholderReference> element in the consumer .plcproj).
#>
param(
    [string]$ProjectPath     = '',
    [string]$PlcName         = $env:PLC_PROJECT_NAME,
    [string]$PlaceholderName,
    [string]$ComVersion      = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode         = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PlaceholderName) { return @{ success = $false; error = 'PlaceholderName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    $libManager = $sm.LookupTreeItem("TIPC^$plc^$plc Project^References")
    $libManager.RemoveReference($PlaceholderName) | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            consumer_plc = $plc
            placeholder  = $PlaceholderName
        }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
