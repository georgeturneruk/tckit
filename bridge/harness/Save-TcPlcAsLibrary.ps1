<#
.SYNOPSIS
    Save a PLC project as a .library file, optionally installing it.

.DESCRIPTION
    Wraps ITcPlcIECProject.SaveAsLibrary(path, install). The PLC project
    tree node ('TIPC^<plc>^<plc> Project') exposes the IEC-project surface;
    PowerShell COM dispatch finds SaveAsLibrary without an explicit cast.

    When -Install is $true, the .library is also registered with the named
    repository in the same COM call (Beckhoff's documented behaviour). The
    library is then resolvable by AddLibrary on any consumer project.

    See ADR-0009.

.PARAMETER ProjectPath
    Absolute path to the .sln file. Falls back to PLC_PROJECT_PATH env var.

.PARAMETER PlcName
    PLC project to save. Optional if exactly one PLC project exists in the
    sln. Falls back to PLC_PROJECT_NAME env var.

.PARAMETER OutputPath
    Absolute path for the generated .library artefact.

.PARAMETER Install
    If $true (default), install into the named repository in the same call.

.PARAMETER Repository
    Library repository name. Default 'System' — the standard TwinCAT
    installed-libraries repo.

.PARAMETER Title
    Library Title metadata. SaveAsLibrary refuses to write a managed
    library when Title is unset, and the Standard PLC Template leaves
    the IEC project's ProjectInfo block empty. Defaults to PlcName.

.PARAMETER Company
    Library Company / distributor metadata. Defaults to 'Tc3 Project'
    to match the AddLibrary distributor default in
    Add-TcLibraryReference.ps1.

.PARAMETER LibraryVersion
    Library Version metadata. Defaults to '1.0.0.0'.
#>
param(
    [string]$ProjectPath    = $env:PLC_PROJECT_PATH,
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$OutputPath,
    [bool]  $Install        = $true,
    [string]$Repository     = 'System',
    [string]$Title          = '',
    [string]$Company        = 'Tc3 Project',
    [string]$LibraryVersion = '1.0.0.0',
    [string]$ComVersion     = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode        = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $OutputPath)  { return @{ success = $false; error = 'OutputPath required.' } }
    if ($Install -and $Repository -ne 'System') {
        return @{
            success = $false
            error   = "Repository '$Repository' not yet supported; v1 supports only 'System'. Pass Install=`$false to skip install."
        }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    # Ensure parent directory exists; SaveAsLibrary will not create it.
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }

    $plcProject = Get-TcPlcProjectNode -SysManager $sm -PlcName $plc

    # SaveAsLibrary refuses to write a managed library if the IEC project's
    # ProjectInfo/Title is empty. The Standard PLC Template doesn't set
    # one; populate Title / Company / Version via ProduceXml + ConsumeXml
    # (the documented metadata round-trip pattern) before SaveAsLibrary.
    if (-not $Title) { $Title = $plc }
    $effectiveTitle   = $Title
    $effectiveCompany = $Company
    $effectiveVersion = $LibraryVersion
    try {
        [xml]$projXml = $plcProject.ProduceXml(0)
        $info = $projXml.SelectSingleNode('//ProjectInfo')
        if ($null -eq $info) {
            return @{ success = $false; error = "ProjectInfo node not found in PLC project XML for '$plc'." }
        }
        $info.SelectSingleNode('Title').InnerText   = $effectiveTitle
        $info.SelectSingleNode('Company').InnerText = $effectiveCompany
        $info.SelectSingleNode('Version').InnerText = $effectiveVersion
        $plcProject.ConsumeXml($projXml.OuterXml) | Out-Null
    } catch {
        return @{ success = $false; error = "Failed to set library metadata: $($_.Exception.Message)" }
    }

    # COM dispatch resolves SaveAsLibrary against ITcPlcIECProject without
    # an explicit cast — same pattern as Set-TcItemSource for the
    # ITcPlcDeclaration / ITcPlcImplementation interfaces.
    $plcProject.SaveAsLibrary($OutputPath, [bool]$Install) | Out-Null
    # The ProduceXml/ConsumeXml round-trip above wrote new ProjectInfo
    # values into the in-memory model; SaveAll flushes them so the
    # .plcproj on disk matches what SaveAsLibrary just emitted.
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            plc          = $plc
            output_path  = $OutputPath
            installed    = [bool]$Install
            repository   = if ($Install) { $Repository } else { $null }
            title        = $effectiveTitle
            company      = $effectiveCompany
            version      = $effectiveVersion
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
