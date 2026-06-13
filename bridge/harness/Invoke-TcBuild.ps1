<#
.SYNOPSIS
    Build a TwinCAT project and return structured errors / warnings.

.DESCRIPTION
    Two-tier strategy:
      1. Attach to XAE, call ITcPlcIECProject2.CheckAllObjects() on the PLC
         project node — fast in-process binary signal of PLC compile state.
      2. If errors are present (or always, if -ForceLog), shell out to
         TcXaeShell.exe /rebuild /log <Log.xml>, then parse the structured
         XML log into BuildError-shaped hashtables.

    Why two tiers? TcXaeShell Express does not expose
    DTE.ToolWindows.ErrorList, so /log is the only way to retrieve
    file/line/message detail. CheckAllObjects gives us a fast happy path.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER ForceLog
    If true, always run devenv /log even if CheckAllObjects() succeeds.
    Useful when the caller wants warnings as well as errors.

.PARAMETER Configuration
    Build configuration. Default 'Release'.

.PARAMETER Platform
    Build platform. Default 'TwinCAT RT (x64)'.

.OUTPUTS
    @{
        success          = bool
        errors           = @( @{file; line; message; severity='error'},   ... )
        warnings         = @( @{file; line; message; severity='warning'}, ... )
        duration_seconds = float
    }
#>
param(
    [string]$ProjectPath   = $env:PLC_PROJECT_PATH,
    [string]$PlcName       = $env:PLC_PROJECT_NAME,
    [bool]  $ForceLog      = $false,
    [string]$Configuration = $(if ($env:TC_BUILD_CONFIG)   { $env:TC_BUILD_CONFIG }   else { 'Release' }),
    [string]$Platform      = $(if ($env:TC_BUILD_PLATFORM) { $env:TC_BUILD_PLATFORM } else { 'TwinCAT RT (x64)' }),
    [string]$ComVersion    = $(if ($env:COM_VERSION)       { $env:COM_VERSION }       else { '17.0' }),
    [string]$XaeMode       = $(if ($env:XAE_MODE)          { $env:XAE_MODE }          else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $ProjectPath) {
        return @{ success = $false; errors = @(); warnings = @(); error = 'ProjectPath required.' }
    }

    $start = Get-Date
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Open-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName
    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName

    # Tier 1 — fast binary signal via CheckAllObjects.
    $checkOk = $false
    try { $checkOk = [bool]$plcProj.CheckAllObjects() } catch {
        # Some failure inside CheckAllObjects itself (rare). Treat as failure.
        $checkOk = $false
    }

    $errors = @()
    $warnings = @()

    # Tier 2 — devenv /log when we need structured detail (errors present, or
    # caller asked for warnings via -ForceLog).
    if (-not $checkOk -or $ForceLog) {
        $logPath = Join-Path $env:TEMP "tckit-build-$([Guid]::NewGuid()).xml"
        try {
            $code = Invoke-TcDevenvBuild -SolutionPath $ProjectPath -LogPath $logPath `
                                         -Configuration $Configuration -Platform $Platform
            $parsed = Read-TcBuildLog -LogPath $logPath
            $errors   = $parsed.errors
            $warnings = $parsed.warnings
            if ($code -ne 0 -and $errors.Count -eq 0) {
                $errors += @{
                    file = ''; line = 0; severity = 'error'
                    message = "devenv.exe /rebuild exit code $code (no structured errors parsed; check $logPath)"
                }
            }
        } finally {
            if (Test-Path $logPath) { Remove-Item $logPath -Force -ErrorAction SilentlyContinue }
        }
    }

    $duration = ((Get-Date) - $start).TotalSeconds
    $success = $checkOk -and ($errors.Count -eq 0)

    return @{
        success          = $success
        errors           = $errors
        warnings         = $warnings
        duration_seconds = [Math]::Round($duration, 2)
        details          = @{ plc = $plcName; check_all_objects = $checkOk }
    }
}
catch {
    return @{
        success  = $false
        errors   = @(@{ file = ''; line = 0; severity = 'error'; message = $_.Exception.Message })
        warnings = @()
        error    = $_.Exception.Message
    }
}
