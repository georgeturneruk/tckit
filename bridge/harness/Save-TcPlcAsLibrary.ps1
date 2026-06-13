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
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

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

.PARAMETER Overwrite
    When $true, delete an existing .library at OutputPath before
    SaveAsLibrary runs. Default $false preserves the underlying COM
    call's "refuse to overwrite" behaviour.
#>
param(
    [string]$ProjectPath    = '',
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$OutputPath,
    [bool]  $Install        = $true,
    [string]$Repository     = 'System',
    [string]$Title          = '',
    [string]$Company        = 'Tc3 Project',
    [string]$LibraryVersion = '1.0.0.0',
    [bool]  $Overwrite      = $false,
    [string]$ComVersion     = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode        = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $OutputPath)  { return @{ success = $false; error = 'OutputPath required.' } }
    if ($Install -and $Repository -ne 'System') {
        return @{
            success = $false
            error   = "Repository '$Repository' not yet supported; v1 supports only 'System'. Pass Install=`$false to skip install."
        }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    # Ensure parent directory exists; SaveAsLibrary will not create it.
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }

    # SaveAsLibrary refuses to overwrite an existing artefact; honour
    # -Overwrite by removing the file first so re-runs of an author
    # script don't need a manual cleanup step.
    if ($Overwrite -and (Test-Path -LiteralPath $OutputPath)) {
        try {
            Remove-Item -LiteralPath $OutputPath -Force -ErrorAction Stop
        } catch {
            return @{
                success = $false
                error   = "Could not remove existing .library at $($OutputPath): $($_.Exception.Message)"
            }
        }
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

    function Invoke-MetadataAndSave {
        param($PlcProject)
        [xml]$projXml = $PlcProject.ProduceXml(0)
        $info = $projXml.SelectSingleNode('//ProjectInfo')
        if ($null -eq $info) {
            throw "ProjectInfo node not found in PLC project XML for '$plc'."
        }
        $info.SelectSingleNode('Title').InnerText   = $effectiveTitle
        $info.SelectSingleNode('Company').InnerText = $effectiveCompany
        $info.SelectSingleNode('Version').InnerText = $effectiveVersion
        $PlcProject.ConsumeXml($projXml.OuterXml) | Out-Null
        # COM dispatch resolves SaveAsLibrary against ITcPlcIECProject
        # without an explicit cast.
        $PlcProject.SaveAsLibrary($OutputPath, [bool]$Install) | Out-Null
    }

    # Cold-start recovery: on a fresh XAE, the placeholder resolver
    # hasn't run yet so PlaceholderReference/EffectiveResolution is
    # null and ProduceXml chokes with an XmlAutomationException
    # pointing at that path. Triggering CheckAllObjects (an in-process
    # PLC compile that runs the resolver as a side effect) lets the
    # second attempt land. Any other exception rethrows unchanged.
    # See ADR-0011.
    $coldStartWarmup = $false
    try {
        Invoke-MetadataAndSave -PlcProject $plcProject
    } catch {
        $msg = [string]$_.Exception.Message
        if ($msg -match 'PlaceholderReference.*EffectiveResolution|EffectiveResolution.*PlaceholderReference') {
            try {
                # Trigger placeholder resolution via an in-process build.
                # If we're in XAE_MODE=headless and SyncLock is wedging
                # the build subsystem (see ADR-0010 status notes), this
                # will throw and we rethrow with the headless-mode hint.
                $plcProject.CheckAllObjects() | Out-Null
            } catch {
                throw "save_plc_as_library cold-start retry failed during warm-up build: $($_.Exception.Message). Headless XAE mode is known-incompatible with cold-start save (Microsoft Visual Studio Appid Stub SyncLock); use XAE_MODE=attach."
            }
            Invoke-MetadataAndSave -PlcProject $plcProject
            $coldStartWarmup = $true
        } else {
            throw
        }
    }

    # The ProduceXml/ConsumeXml round-trip above wrote new ProjectInfo
    # values into the in-memory model; SaveAll flushes them so the
    # .plcproj on disk matches what SaveAsLibrary just emitted.
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            plc                = $plc
            output_path        = $OutputPath
            installed          = [bool]$Install
            repository         = if ($Install) { $Repository } else { $null }
            title              = $effectiveTitle
            company            = $effectiveCompany
            version            = $effectiveVersion
            cold_start_warmup  = $coldStartWarmup
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
