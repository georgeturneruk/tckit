<#
.SYNOPSIS
    Add a folder to a PLC project's source tree.

.DESCRIPTION
    Creates a folder tree item (ItemType 601) under either the POUs
    or DUTs subtree of a PLC project. Nested folders are supported by
    passing the existing path under that root via -ParentFolder; the
    intermediate folders must already exist (use repeated add_folder
    calls bottom-up).

    Beckhoff samples confirm the canonical call shape is
    parent.CreateChild(name, 601, null, null); the 4th vInfo argument
    is documented as null for folder creation. Tree-item kinds and the
    Folder constant are sourced from
    https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242732427.html
    via TC_AI_DOTNET_Samples/ItemTypes.cs.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls
    back to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the new folder.

.PARAMETER ParentPath
    Path under the PLC project's IDE-level node where the folder
    should live, slash-separated. Examples: ``POUs``, ``POUs/Drives``,
    ``DUTs``, ``DUTs/Motors``. Defaults to ``POUs``.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$ParentPath  = 'POUs',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName

    # ParentPath is an under-the-PLC-project path: the first segment names
    # the root subtree (POUs / DUTs), subsequent segments are existing
    # folders. We walk it directly from the PLC project node so callers
    # don't have to remember the TIPC^... prefix.
    $parent = Resolve-TcFolderPath -Root $plcProj -Path $ParentPath
    if ($null -eq $parent) {
        return @{ success = $false; error = "ParentPath '$ParentPath' did not resolve under '$plcName'." }
    }

    $folderKind = Get-TcKind -Type 'folder'
    $newFolder = $parent.CreateChild($Name, $folderKind, $null, $null)
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; parent_path = $ParentPath; plc = $plcName; kind = $folderKind }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
