<#
.SYNOPSIS
    Add a second TwinCAT project + PLC to an existing TwinCAT solution.

.DESCRIPTION
    Each PLC lives inside its own TwinCAT project (one .tsproj per PLC,
    multiple .tsprojs per .sln). This matches the layout the
    File → New → TwinCAT XAE Project wizard produces and is what
    round-trips cleanly through Solution.Open from disk. The earlier
    pattern of stacking two PLCs under one .tspproj's TIPC node skips
    the <Instance> block on save and crashes XAE in
    IVsParentProject.OpenChildren() on reload.

    Steps:
      1. Open the solution (idempotent via Open-TcSolution).
      2. Solution.AddFromTemplate(<TwinCAT Project.tsproj>, <sln_dir>\<PlcName>,
         <PlcName>, $false) so a second TwinCAT project (named after the PLC)
         lands in its own subdirectory at sln level.
      3. Get the new sysmanager's TIPC and CreateChild for the PLC named
         <PlcName>.
      4. Solution.SaveAs(<existing .sln path>) so the .sln file records the
         new project entry.

    See ADR-0009.

.PARAMETER ProjectPath
    Absolute path to the existing .sln file.

.PARAMETER PlcName
    Name of the new PLC sub-project. The wrapping TwinCAT project gets a
    suffix ("<PlcName>_Tc") so its name doesn't collide with the PLC's;
    same-name objects at different tree levels have crashed XAE on save
    (see PR #77 — analogous collision between sln and first PLC).
    Must not collide with an existing PLC name in any TwinCAT project
    in the same sln.

.PARAMETER ProjectType
    Reserved. v1 only accepts 'standard'; passing 'library' returns an
    explicit error. Default 'standard'.

.PARAMETER TemplatePath
    Optional explicit template .tsproj path. If omitted, the standard 4026
    install location is used.
#>
param(
    [string]$ProjectPath,
    [string]$PlcName,
    [string]$ProjectType  = 'standard',
    [string]$TemplatePath = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) { return @{ success = $false; error = 'ProjectPath required.' } }
    if (-not $PlcName)     { return @{ success = $false; error = 'PlcName required.' } }
    if ($ProjectType -ne 'standard') {
        return @{ success = $false; error = "ProjectType '$ProjectType' not supported (only 'standard')." }
    }

    if (-not $TemplatePath) {
        $TemplatePath = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PrjTemplate\TwinCAT Project.tsproj'
    }
    if (-not (Test-Path $TemplatePath)) {
        return @{ success = $false; error = "Template not found: $TemplatePath. Pass -TemplatePath." }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null

    # Guard against PlcName collision against every existing TwinCAT project.
    $existing = Get-TcSysManagers -Dte $dte
    foreach ($sm in $existing) {
        try {
            $tipc = $sm.LookupTreeItem('TIPC')
            for ($i = 1; $i -le $tipc.ChildCount; $i++) {
                if ($tipc.Child($i).Name -eq $PlcName) {
                    return @{ success = $false; error = "PLC project '$PlcName' already exists in solution." }
                }
            }
        } catch { continue }
    }

    # Step 2: add a second TwinCAT project at sln level, in its own subdir.
    # The TwinCAT project gets a "_Tc" suffix so its name doesn't collide
    # with the PLC's; XAE crashes on save when an object's name matches
    # one of its descendants' (same class of collision as PR #77).
    $slnDir = Split-Path -Parent (Resolve-Path $ProjectPath).Path
    $tcProjectName = "${PlcName}_Tc"
    $tcProjectDir = Join-Path $slnDir $tcProjectName
    $dte.Solution.AddFromTemplate($TemplatePath, $tcProjectDir, $tcProjectName, $false) | Out-Null

    # Step 3: find the new sysmanager (the one whose TIPC has no children
    # yet — the only freshly-added .tsproj) and add the PLC under it.
    # AddFromTemplate returns before XAE has finished exposing the new
    # EnvDTE Project; Get-TcSysManagers may still see only the existing
    # one. Poll until an empty-TIPC sysmanager appears.
    $newSm = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        $managers = Get-TcSysManagers -Dte $dte
        foreach ($sm in $managers) {
            try {
                if ($sm.LookupTreeItem('TIPC').ChildCount -eq 0) { $newSm = $sm; break }
            } catch { continue }
        }
        if ($null -ne $newSm) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $newSm) {
        return @{ success = $false; error = "Could not locate the new TwinCAT project's empty TIPC after AddFromTemplate." }
    }
    $tipc = $newSm.LookupTreeItem('TIPC')
    $tipc.CreateChild($PlcName, 0, $null, 'Standard PLC Template.plcproj') | Out-Null

    # Step 4: persist back to the existing sln file. SaveAs against the
    # current FullName updates the .sln with the new Project() entry.
    # SaveAs alone does NOT flush the new .tsproj's <System>/<Plc>/<Instance>
    # to disk; File.SaveAll does. Without it XAE segfaults on reload (see
    # New-TcProject.ps1 for the same rationale).
    $dte.Solution.SaveAs($dte.Solution.FullName) | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            solution_path = $ProjectPath
            plc           = $PlcName
            project_type  = $ProjectType
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
