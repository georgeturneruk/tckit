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
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$OutputPath,
    [bool]  $Install     = $true,
    [string]$Repository  = 'System',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
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
    $sm = Get-TcSysManager -Dte $dte
    $plc = Resolve-TcPlcName -SysManager $sm -Explicit $PlcName

    # Ensure parent directory exists; SaveAsLibrary will not create it.
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }

    $plcProject = Get-TcPlcProjectNode -SysManager $sm -PlcName $plc
    # COM dispatch resolves SaveAsLibrary against ITcPlcIECProject without
    # an explicit cast — same pattern as Set-TcItemSource for the
    # ITcPlcDeclaration / ITcPlcImplementation interfaces.
    $plcProject.SaveAsLibrary($OutputPath, [bool]$Install)

    return @{
        success = $true
        details = @{
            plc          = $plc
            output_path  = $OutputPath
            installed    = [bool]$Install
            repository   = if ($Install) { $Repository } else { $null }
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
