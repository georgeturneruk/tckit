<#
.SYNOPSIS
    Run a closed-loop bench task (T1 or B1) against tckit, vanilla, or both.

.DESCRIPTION
    Wraps bench/run.py with the right per-task flags so an operator does
    not have to copy-paste eight arguments and remember which probes go
    with which fixture. Mirrors the invocation shape documented in
    bench/README.md plus the per-task tweaks from the 2026-05-16 T1 and
    B1 finding files.

    Preconditions:
      - PowerShell working directory is the tckit repo root (the script
        will refuse to run from elsewhere).
      - The bridge is reachable at -BridgeUrl. Start it with
        .\bridge\Start-Bridge.ps1 if it is not.
      - TcXaeShell is running (attach mode). If not, the bridge will
        spawn one when it first needs DTE.
      - For the tckit arm, the MCP server is reachable at -McpUrl. If
        absent and -StartMcp is given, the script starts one in the
        background and tears it down on exit.

    Self-validation: the model session normally hits the safety-gate
    handshake on deploy / start_runtime and waits for human approval.
    In a claude -p non-interactive session there is no human, so the
    model hands off to the harness instead of self-validating. Pass
    -SelfValidate to start the MCP server with SAFETY_CONFIRMATIONS=false
    so the gate passes through without approval. Only do this on a dev
    machine where you are happy for the bench to talk freely to
    -TargetAmsId.

.PARAMETER Task
    T1 (Schmitt-trigger TDD pair) or B1 (rolling-average off-by-one).

.PARAMETER Arm
    Which arm to run: tckit, vanilla, or both. Default both.

.PARAMETER StartMcp
    If set, start the tckit MCP server in the background when the tckit
    arm runs, and kill it on exit. Without this flag the script expects
    an MCP server already listening at -McpUrl.

.PARAMETER SelfValidate
    If set, MCP server starts with SAFETY_CONFIRMATIONS=false so the
    model can call deploy / start_runtime without the approval
    handshake. Otherwise sets ALLOWED_NETIDS=<TargetAmsId> as a
    narrower allow-list. Dev-machine only.

.PARAMETER TargetAmsId
    AMS Net ID the model targets. Default 127.0.0.1.1.1 (local UmRT).

.PARAMETER BridgeUrl
    Bridge URL. Default http://localhost:8765.

.PARAMETER McpUrl
    MCP server URL. Default http://localhost:8000.

.PARAMETER Runs
    Passed through to --runs. Default 1.

.EXAMPLE
    # Most common: run both arms of T1 with the MCP server I'll auto-start.
    .\bench\run-pair.ps1 -Task T1 -StartMcp -SelfValidate

.EXAMPLE
    # Just the tckit arm of B1, MCP server already up.
    .\bench\run-pair.ps1 -Task B1 -Arm tckit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('T1','B1')][string]$Task,
    [ValidateSet('tckit','vanilla','both')][string]$Arm = 'both',
    [switch]$StartMcp,
    [switch]$SelfValidate,
    [string]$TargetAmsId = '127.0.0.1.1.1',
    [string]$BridgeUrl   = 'http://localhost:8765',
    [string]$McpUrl      = 'http://localhost:8000',
    [int]   $Runs        = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Per-task config table
# ---------------------------------------------------------------------------
$tasks = @{
    'T1' = @{
        Dir          = 'bench/fixtures/bug-hunting/T1-schmitt-trigger'
        Sln          = 'bench/fixtures/bug-hunting/T1-schmitt-trigger/T1SchmittTrigger.sln'
        LibraryPlc   = 'T1SchmittTrigger_Plc'
        TestsPlc     = 'SchmittTriggerTests'
        TestsPouPath = 'bench/fixtures/bug-hunting/T1-schmitt-trigger/SchmittTriggerTests_Tc/SchmittTriggerTests/POUs/'
        Probes       = @(
            'MAIN.suite.Tests[1].TestIsFailed',
            'MAIN.suite.Tests[2].TestIsFailed',
            'MAIN.suite.Tests[3].TestIsFailed',
            'MAIN.suite.Tests[4].TestIsFailed',
            'MAIN.suite.Tests[5].TestIsFailed',
            'MAIN.suite.NumberOfTests'
        )
    }
    'B1' = @{
        Dir          = 'bench/fixtures/bug-hunting/B1-off-by-one'
        Sln          = 'bench/fixtures/bug-hunting/B1-off-by-one/B1RollingAverage.sln'
        LibraryPlc   = 'B1RollingAverage_Plc'
        TestsPlc     = 'RollingAverageTests'
        TestsPouPath = 'bench/fixtures/bug-hunting/B1-off-by-one/RollingAverageTests_Tc/RollingAverageTests/POUs/'
        Probes       = @(
            'MAIN.suite.Tests[1].TestIsFailed',
            'MAIN.suite.NumberOfTests'
        )
    }
}
$cfg = $tasks[$Task]

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------
if (-not (Test-Path 'bench/run.py')) {
    throw "Run from tckit repo root (cwd: $((Get-Location).Path))."
}
$repoRoot = (Get-Location).Path

try {
    $health = Invoke-RestMethod -Uri "$BridgeUrl/health" -TimeoutSec 5
    Write-Host "[OK] bridge $($health.version) at $BridgeUrl"
} catch {
    throw "Bridge not reachable at $BridgeUrl. Start with .\bridge\Start-Bridge.ps1"
}

# ---------------------------------------------------------------------------
# MCP server lifecycle (only needed for the tckit arm)
# ---------------------------------------------------------------------------
$mcpProcess = $null
$needsMcp = ($Arm -eq 'tckit' -or $Arm -eq 'both')

if ($needsMcp) {
    $mcpReachable = $false
    try {
        $null = Invoke-WebRequest -Uri "$McpUrl/sse" -TimeoutSec 2 -UseBasicParsing
        $mcpReachable = $true
    } catch { }

    if ($mcpReachable) {
        Write-Host "[OK] MCP server at $McpUrl"
    } elseif ($StartMcp) {
        Write-Host "Starting MCP server (cleanup on exit)..."
        $mcpEnv = @{
            TARGET_AMS_ID    = $TargetAmsId
            PLC_PROJECT_PATH = "$repoRoot/$($cfg.Sln)" -replace '\\','/'
        }
        if ($SelfValidate) {
            $mcpEnv.SAFETY_CONFIRMATIONS = 'false'
            Write-Host "  self-validate: SAFETY_CONFIRMATIONS=false (dev machine only)"
        } else {
            $mcpEnv.ALLOWED_NETIDS = $TargetAmsId
            Write-Host "  approval gate: ALLOWED_NETIDS=$TargetAmsId"
        }
        $envPrefix = ($mcpEnv.GetEnumerator() | ForEach-Object { "`$env:$($_.Key)='$($_.Value)'" }) -join '; '
        $cmd = "$envPrefix; uv run python -m tckit.server --transport sse"
        $mcpProcess = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command', $cmd `
            -WindowStyle Hidden -PassThru
        # Wait for it to listen
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            try {
                $null = Invoke-WebRequest -Uri "$McpUrl/sse" -TimeoutSec 2 -UseBasicParsing
                break
            } catch { Start-Sleep -Milliseconds 500 }
        }
        Write-Host "[OK] MCP server up (PID $($mcpProcess.Id))"
    } else {
        throw "MCP server not reachable at $McpUrl. Pass -StartMcp to auto-start, or start manually: uv run python -m tckit.server --transport sse"
    }
}

# ---------------------------------------------------------------------------
# Build the shared bench/run.py argument list
# ---------------------------------------------------------------------------
$resetCmd = "git -C $repoRoot checkout HEAD -- $($cfg.Dir)"

$commonArgs = @(
    '--task',                "$($cfg.Dir)/TASK.md",
    '--runs',                $Runs.ToString(),
    '--tcunit-path',         $cfg.Dir,
    '--sln-path',            $cfg.Sln,
    '--reset-cmd',           $resetCmd,
    '--pre-save-as-library', $cfg.LibraryPlc,
    '--post-run-tests',      $cfg.TestsPlc,
    '--tests-guard-path',    $cfg.TestsPouPath,
    '--close-during-run',
    '--isolate-cwd'
)
foreach ($probe in $cfg.Probes) { $commonArgs += @('--test-probe', $probe) }

# ---------------------------------------------------------------------------
# Run the arms
# ---------------------------------------------------------------------------
$env:TARGET_AMS_ID    = $TargetAmsId
$env:PLC_PROJECT_PATH = "$repoRoot/$($cfg.Sln)" -replace '\\','/'

function Invoke-Arm {
    param([string]$Config, [string[]]$ExtraArgs)
    $argv = @('bench/run.py', '--config', "bench/configs/$Config.json") + $commonArgs + $ExtraArgs
    Write-Host ''
    Write-Host "=== $Task arm: $Config ==="
    & uv run python @argv
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$Config arm exited $LASTEXITCODE"
    }
}

try {
    if ($Arm -in 'tckit','both') {
        Invoke-Arm -Config 'tckit' -ExtraArgs @('--inject-skills', 'plugin/skills')
    }
    if ($Arm -in 'vanilla','both') {
        Invoke-Arm -Config 'empty' -ExtraArgs @()
    }
}
finally {
    if ($mcpProcess) {
        Write-Host ''
        Write-Host "Stopping MCP server (PID $($mcpProcess.Id))..."
        try { Stop-Process -Id $mcpProcess.Id -Force -ErrorAction Stop } catch { }
    }
}

Write-Host ''
Write-Host "Done. Results: bench/results/TASK__*__$($Task)*.json (and .md / .diff / .test-result.json siblings)."
