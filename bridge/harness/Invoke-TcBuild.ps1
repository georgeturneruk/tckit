<#
.SYNOPSIS
    Build a TwinCAT project via the XAE COM automation interface.

.DESCRIPTION
    Attaches to (or spawns) TcXaeShell.DTE.17.0, opens the solution,
    triggers a build, and returns structured errors as JSON.

    Not yet implemented — returns stub response.
#>
param(
    [string]$ProjectPath = $env:PLC_PROJECT_PATH,
    [string]$ComVersion  = ($env:COM_VERSION ?? '17.0'),
    [string]$XaeMode     = ($env:XAE_MODE ?? 'attach')
)

# TODO Phase 2: implement COM attach, build trigger, error parsing
return @{
    success = $false
    errors  = @()
    error   = 'Invoke-TcBuild.ps1 not yet implemented'
}
