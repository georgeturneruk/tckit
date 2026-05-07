<#
.SYNOPSIS
    Phase 2 spike: validate template-based PLC project creation via DTE.

.DESCRIPTION
    Discovers the standard TwinCAT PLC project template path on this install
    and attempts to create a throwaway PLC project at -OutPath. This validates
    the contract that New-TcProject.ps1 will rely on.

    Pass -DryRun to only print discovered template paths without creating
    anything.

.PARAMETER OutPath
    Directory in which to create the spike project. Default: %TEMP%\tckit_spike_newproject.

.PARAMETER ProjectName
    Name of the spike project. Default: SpikeProject.

.PARAMETER DryRun
    If set, only enumerates candidate template paths and exits.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.EXAMPLE
    .\scripts\spike-newproject.ps1 -DryRun
    .\scripts\spike-newproject.ps1
#>
param(
    [string]$OutPath     = (Join-Path $env:TEMP 'tckit_spike_newproject'),
    [string]$ProjectName = 'SpikeProject',
    [switch]$DryRun,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' })
)

$ErrorActionPreference = 'Stop'

$progId = "TcXaeShell.DTE.$ComVersion"
Write-Host "Attaching to $progId..."
$dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)

# Search likely install locations for TwinCAT project template files.
$templateRoots = @(
    'C:\TwinCAT\3.1\Components\Plc\PlcTemplate',
    'C:\TwinCAT\3.1\Config\Plc\PlcTemplate',
    "$env:ProgramFiles (x86)\Beckhoff\TwinCAT\3.1\Components\Plc\PlcTemplate",
    "$env:ProgramFiles\Beckhoff\TwinCAT\3.1\Components\Plc\PlcTemplate"
)

Write-Host ''
Write-Host '=== Searching for PLC project templates ==='
$found = @()
foreach ($root in $templateRoots) {
    if (Test-Path $root) {
        Write-Host "Found: $root"
        $found += $root
    } else {
        Write-Host "  (missing) $root"
    }
}

# Also try GetProjectTemplate, which is the documented DTE way.
try {
    Write-Host ''
    Write-Host 'Trying DTE.Solution.GetProjectTemplate("PlcTemplate.tsproj", "TcXaeShell")...'
    $tplPath = $dte.Solution.GetProjectTemplate('PlcTemplate.tsproj', 'TcXaeShell')
    Write-Host "  -> $tplPath"
} catch {
    Write-Host "  GetProjectTemplate failed: $_" -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'DryRun set — exiting without creating project.'
    return
}

if ($found.Count -eq 0 -and $null -eq $tplPath) {
    throw 'No template path discovered. Update template search list.'
}

if (-not (Test-Path $OutPath)) {
    New-Item -ItemType Directory -Path $OutPath -Force | Out-Null
}

$useTemplate = if ($tplPath) { $tplPath } else { Join-Path $found[0] 'PlcTemplate.tsproj' }
Write-Host ''
Write-Host "Creating project '$ProjectName' at '$OutPath' using template:"
Write-Host "  $useTemplate"

try {
    $dte.Solution.AddFromTemplate($useTemplate, $OutPath, $ProjectName, $false)
    Write-Host 'AddFromTemplate succeeded.'
    $solutionPath = Join-Path $OutPath ("$ProjectName.sln")
    Write-Host "Expected solution path: $solutionPath"
} catch {
    Write-Host "AddFromTemplate FAILED: $_" -ForegroundColor Red
    throw
}

Write-Host ''
Write-Host 'Spike complete. Update SPIKE_NOTES.md with the working template path and signature.'
