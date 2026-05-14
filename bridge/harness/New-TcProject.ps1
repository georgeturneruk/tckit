<#
.SYNOPSIS
    Create a new TwinCAT solution + PLC project from the standard template.

.DESCRIPTION
    Recipe:

      1. Solution.Create(<dir>, <name>)
      2. Solution.AddFromTemplate(<TwinCAT Project.tsproj>, <dir>\<name>, <name>, $false)
      3. tipc.CreateChild(<plcName>, 0, $null, 'Standard PLC Template.plcproj')
      4. Solution.SaveAs(<dir>\<name>.sln)

    The destination directory for AddFromTemplate is a subdirectory named
    after the TwinCAT project (matching the "File → New → TwinCAT XAE
    Project" wizard layout), so the on-disk shape is:

      <dir>\<name>.sln
      <dir>\<name>\<name>.tsproj
      <dir>\<name>\<plcName>\<plcName>.plcproj

    Add-TcPlcProject extends this by placing each additional TwinCAT
    project (one per PLC) in its own subdirectory at sln level. We use
    the FULL TwinCAT project template (`.tsproj`) rather than the
    PLC-only template (`.tspproj`); the PLC-only template doesn't
    persist the System Manager <Instance> block on save, which makes
    XAE segfault in `IVsParentProject.OpenChildren()` when reloading
    the sln from disk.

.PARAMETER Name
    Solution name (also used for the .sln file and the first TwinCAT project).

.PARAMETER Path
    Directory in which to create the solution.

.PARAMETER PlcName
    Name of the PLC sub-project under the first TwinCAT project. Defaults
    to "${Name}_Plc". Same-name collisions between sln / TwinCAT project /
    PLC have crashed TcXaeShell on load; the default keeps them distinct.

.PARAMETER TemplatePath
    Optional explicit template .tsproj path. If omitted, the standard 4026
    install location is used.
#>
param(
    [string]$Name,
    [string]$Path,
    [string]$PlcName      = '',
    [string]$TemplatePath = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name) { return @{ success = $false; error = 'Name required.' } }
    if (-not $Path) { return @{ success = $false; error = 'Path required.' } }
    if (-not $PlcName) { $PlcName = "${Name}_Plc" }

    if (-not $TemplatePath) {
        $TemplatePath = 'C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PrjTemplate\TwinCAT Project.tsproj'
    }
    if (-not (Test-Path $TemplatePath)) {
        return @{ success = $false; error = "Template not found: $TemplatePath. Pass -TemplatePath." }
    }
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode

    if ($null -eq $dte.Solution) {
        return @{
            success = $false
            error   = 'TcXaeShell DTE has no Solution object (attached but uninitialised). Restart TcXaeShell and retry.'
        }
    }

    # Step 1: empty solution shell. COM methods on Solution emit objects
    # into the PowerShell output stream; suppress so the trailing hashtable
    # is the only value the harness returns.
    #
    # On a pre-loaded XAE Solution.Create throws because something's
    # already there. Try Create directly first; if it fails, close
    # any loaded sln and retry once.
    try {
        $dte.Solution.Create($Path, $Name) | Out-Null
    } catch {
        try { $dte.Solution.Close($false) | Out-Null } catch { }
        $dte.Solution.Create($Path, $Name) | Out-Null
    }

    # Step 2: TwinCAT project from the full .tsproj template. Destination
    # is a subdir named after the TwinCAT project.
    $tcProjectDir = Join-Path $Path $Name
    $dte.Solution.AddFromTemplate($TemplatePath, $tcProjectDir, $Name, $false) | Out-Null

    # Step 3: PLC sub-project under that new TwinCAT project's TIPC.
    $sm = Get-TcSysManager -Dte $dte
    $tipc = $sm.LookupTreeItem('TIPC')
    $tipc.CreateChild($PlcName, 0, $null, 'Standard PLC Template.plcproj') | Out-Null

    # Step 4: persist the solution. Solution.SaveAs alone does NOT flush the
    # full <System>/<Plc>/<Instance> structure into the .tsproj on disk —
    # the wizard does this via File.SaveAll under the hood. Without it the
    # on-disk .tsproj is just the empty 4-line template, and XAE
    # segfaults in IVsParentProject.OpenChildren when reloading.
    $solutionPath = Join-Path $Path ("$Name.sln")
    $dte.Solution.SaveAs($solutionPath) | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ solution_path = $solutionPath; plc = $PlcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
