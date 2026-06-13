<#
.SYNOPSIS
    Build a TwinCAT project and return structured errors / warnings.

.DESCRIPTION
    Two-tier strategy:
      1. Attach to XAE, call ITcPlcIECProject2.CheckAllObjects() on the PLC
         project node — fast in-process binary signal of PLC compile state.
         This also populates the IDE Error List.
      2. If the build failed (or always, if -ForceLog), read structured
         diagnostics. Prefer the IDE Error List (the actual PLC errors /
         warnings / infos with file, line, code and project); fall back to
         a /out build-output parse on editions that don't expose
         DTE.ToolWindows.ErrorList (TcXaeShell Express).

    The earlier implementation parsed the /log *activity* log, which records
    IDE startup events rather than PLC diagnostics — so a failing build
    returned no usable errors.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER ForceLog
    If true, always read diagnostics even if CheckAllObjects() succeeds.
    Useful when the caller wants warnings / infos as well as errors.

.PARAMETER Configuration
    Build configuration. Default 'Release'.

.PARAMETER Platform
    Build platform. Default 'TwinCAT RT (x64)'.

.OUTPUTS
    @{
        success          = bool
        errors           = @( @{file; line; message; severity='error'; code; project},   ... )
        warnings         = @( @{file; line; message; severity='warning'; code; project}, ... )
        infos            = @( @{file; line; message; severity='info'; code; project},    ... )
        duration_seconds = float
    }
#>
param(
    [string]$ProjectPath   = '',
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
    $start = Get-Date
    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    if (-not $ProjectPath) { $ProjectPath = $dte.Solution.FullName }
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName
    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName

    # Tier 1 — fast binary signal via CheckAllObjects. This compiles the PLC
    # project and populates the IDE Error List.
    $checkOk = $false
    try { $checkOk = [bool]$plcProj.CheckAllObjects() } catch {
        # Some failure inside CheckAllObjects itself (rare). Treat as failure.
        $checkOk = $false
    }

    $errors = @()
    $warnings = @()
    $infos = @()

    # Tier 2 — pull structured diagnostics when the build failed, or the
    # caller asked for warnings via -ForceLog. Prefer the IDE Error List
    # (the real PLC diagnostics); fall back to a /out build-output parse on
    # editions that don't expose ToolWindows.ErrorList (Express).
    if (-not $checkOk -or $ForceLog) {
        $el = Read-TcErrorList -Dte $dte
        if ($null -ne $el) {
            $errors   = @($el.errors)
            $warnings = @($el.warnings)
            $infos    = @($el.infos)
        } else {
            $outPath = Join-Path $env:TEMP "tckit-build-$([Guid]::NewGuid()).txt"
            try {
                $code = Invoke-TcDevenvBuild -SolutionPath $ProjectPath -OutPath $outPath `
                                             -Configuration $Configuration -Platform $Platform
                $parsed = Read-TcBuildOutput -OutPath $outPath
                $errors   = @($parsed.errors)
                $warnings = @($parsed.warnings)
                if ($code -ne 0 -and $errors.Count -eq 0) {
                    $errors += @{
                        file = ''; line = 0; severity = 'error'; code = ''; project = ''
                        message = "devenv.exe /rebuild exit code $code (no structured diagnostics parsed; check $outPath)"
                    }
                }
            } finally {
                if (Test-Path $outPath) { Remove-Item $outPath -Force -ErrorAction SilentlyContinue }
            }
        }
    }

    $duration = ((Get-Date) - $start).TotalSeconds
    $success = $checkOk -and ($errors.Count -eq 0)

    return @{
        success          = $success
        errors           = $errors
        warnings         = $warnings
        infos            = $infos
        duration_seconds = [Math]::Round($duration, 2)
        details          = @{ plc = $plcName; check_all_objects = $checkOk }
    }
}
catch {
    return @{
        success  = $false
        errors   = @(@{ file = ''; line = 0; severity = 'error'; code = ''; project = ''; message = $_.Exception.Message })
        warnings = @()
        infos    = @()
        error    = $_.Exception.Message
    }
}
