<#
.SYNOPSIS
    Run a closed-loop bench task against tckit, vanilla, or both arms.

.DESCRIPTION
    Wraps bench/run.py with the per-task flags so an operator does
    not have to copy-paste eight arguments and remember which probes
    go with which fixture. Mirrors the invocation shape documented
    in bench/README.md plus the per-task tweaks captured in the
    individual finding files.

    Per-task config is data-driven: each fixture provides its own
    `bench-config.json` next to TASK.md describing the sln, the
    library / tests PLC names, the tests POU path (for the tamper
    guard), and the test probes to read after each run. Adding a
    new fixture only requires dropping in a new fixture directory
    with a TASK.md + bench-config.json; no script edit needed.

    Preconditions:
      - PowerShell working directory is the tckit repo root (the
        script will refuse to run from elsewhere).
      - The bridge is reachable at -BridgeUrl. Start it with
        .\bridge\Start-Bridge.ps1 if it is not.
      - TcXaeShell is running (attach mode) with no solution loaded.
        The script will open and close the bench sln via the bridge.
      - The MCP port (-McpUrl, default 8000) is FREE. The bench
        spawns its own MCP server per run and /opens the active
        fixture sln (temp under --isolate-cwd, else the real sln)
        so the server targets it. If you have an interactive MCP
        server running on 8000, stop it first or pass -McpUrl
        pointing at a free port.

    Self-validation: the model session normally hits the safety-gate
    handshake on deploy / start_runtime and waits for human approval.
    In a claude -p non-interactive session there is no human, so the
    model hands off to the harness instead of self-validating. Pass
    -SelfValidate to start the per-run MCP server with
    SAFETY_CONFIRMATIONS=false so the gate passes through without
    approval. Only do this on a dev machine where you are happy for
    the bench to talk freely to -TargetAmsId.

.PARAMETER Task
    Fixture directory name under bench/fixtures/bug-hunting/. Any
    fixture with a bench-config.json next to its TASK.md is valid
    (e.g. 'T1-schmitt-trigger', 'B1-off-by-one', 'T2-pid-anti-windup').

.PARAMETER Arm
    Which arm to run: tckit, vanilla, or both. Default both.

.PARAMETER SelfValidate
    If set, the per-run MCP server starts with
    SAFETY_CONFIRMATIONS=false so the model can call deploy /
    start_runtime without the approval handshake. Otherwise sets
    ALLOWED_NETIDS=<TargetAmsId> as a narrower allow-list.
    Dev-machine only.

.PARAMETER TargetAmsId
    AMS Net ID the model targets. Default 127.0.0.1.1.1 (local UmRT).

.PARAMETER BridgeUrl
    Bridge URL. Default http://localhost:8765.

.PARAMETER McpUrl
    MCP server URL. Default http://localhost:8000.

.PARAMETER Runs
    Passed through to --runs. Default 1.

.EXAMPLE
    # Both arms of T1, self-validating (model can deploy).
    .\bench\run-bench.ps1 -Task T1-schmitt-trigger -SelfValidate

.EXAMPLE
    # Just the tckit arm of T2 with the deploy safety gate engaged.
    .\bench\run-bench.ps1 -Task T2-pid-anti-windup -Arm tckit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ArgumentCompleter({
        param($cmd, $param, $word)
        $root = Join-Path (Get-Location) 'bench/fixtures/bug-hunting'
        if (-not (Test-Path $root)) { return }
        Get-ChildItem -Path $root -Directory |
            Where-Object { Test-Path (Join-Path $_.FullName 'bench-config.json') } |
            Where-Object { $_.Name -like "$word*" } |
            ForEach-Object { $_.Name }
    })]
    [string]$Task,
    [ValidateSet('tckit','vanilla','both')][string]$Arm = 'both',
    [switch]$SelfValidate,
    [string]$TargetAmsId = '127.0.0.1.1.1',
    [string]$BridgeUrl   = 'http://localhost:8765',
    [string]$McpUrl      = 'http://localhost:8000',
    [int]   $Runs        = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Pre-flight: repo root + bridge reachability
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
# Resolve per-task config from bench-config.json
# ---------------------------------------------------------------------------
$taskDir = "bench/fixtures/bug-hunting/$Task"
if (-not (Test-Path $taskDir)) {
    throw "Fixture directory not found: $taskDir. Available: $(Get-ChildItem 'bench/fixtures/bug-hunting' -Directory | ForEach-Object Name | Sort-Object | Join-String -Separator ', ')"
}
$manifestPath = Join-Path $taskDir 'bench-config.json'
if (-not (Test-Path $manifestPath)) {
    throw "No bench-config.json at $manifestPath. Create one with sln/libraryPlc/testsPlc/testsPouPath/probes fields (see T1-schmitt-trigger for the shape)."
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

foreach ($field in @('sln','libraryPlc','testsPlc','testsPouPath','probes')) {
    if (-not (Get-Member -InputObject $manifest -Name $field -MemberType NoteProperty)) {
        throw "bench-config.json at $manifestPath is missing required field '$field'."
    }
}

$slnPath      = "$taskDir/$($manifest.sln)"
$testsPouPath = "$taskDir/$($manifest.testsPouPath)"
$probes       = @($manifest.probes)

Write-Host "[OK] task $Task -> sln=$slnPath, libraryPlc=$($manifest.libraryPlc), testsPlc=$($manifest.testsPlc), $($probes.Count) probes"

# ---------------------------------------------------------------------------
# MCP port pre-check (bench owns MCP lifecycle per run; refuse on collision)
# ---------------------------------------------------------------------------
$needsMcp = ($Arm -eq 'tckit' -or $Arm -eq 'both')

if ($needsMcp) {
    $mcpUri = [Uri]$McpUrl
    $existing = Get-NetTCPConnection -State Listen -LocalPort $mcpUri.Port -ErrorAction SilentlyContinue
    if ($existing) {
        throw "$($mcpUri.Host):$($mcpUri.Port) is already in use. Stop the existing MCP server (the bench manages MCP lifecycle per run and /opens the right sln; sharing with an interactive MCP would hit the isolate-cwd staleness bug)."
    }
}

# ---------------------------------------------------------------------------
# Bench env: forwarded into bench/run.py and inherited by each per-run MCP
# spawn (start_mcp_subprocess copies parent env). SAFETY_CONFIRMATIONS or
# ALLOWED_NETIDS is the only thing that lets the model self-validate in a
# claude -p non-interactive session.
# ---------------------------------------------------------------------------
$env:TARGET_AMS_ID    = $TargetAmsId
if ($SelfValidate) {
    $env:SAFETY_CONFIRMATIONS = 'false'
    Remove-Item Env:ALLOWED_NETIDS -ErrorAction SilentlyContinue
    Write-Host "Mode: self-validate (SAFETY_CONFIRMATIONS=false, dev machine only)"
} else {
    $env:ALLOWED_NETIDS = $TargetAmsId
    Remove-Item Env:SAFETY_CONFIRMATIONS -ErrorAction SilentlyContinue
    Write-Host "Mode: gated (ALLOWED_NETIDS=$TargetAmsId)"
}

# ---------------------------------------------------------------------------
# Build the shared bench/run.py argument list
# ---------------------------------------------------------------------------
$resetCmd = "git -C $repoRoot checkout HEAD -- $taskDir"

$commonArgs = @(
    '--task',                "$taskDir/TASK.md",
    '--runs',                $Runs.ToString(),
    '--tcunit-path',         $taskDir,
    '--sln-path',            $slnPath,
    '--reset-cmd',           $resetCmd,
    '--pre-save-as-library', $manifest.libraryPlc,
    '--post-run-tests',      $manifest.testsPlc,
    '--tests-guard-path',    $testsPouPath,
    '--close-during-run',
    '--isolate-cwd'
)
foreach ($probe in $probes) { $commonArgs += @('--test-probe', $probe) }

# ---------------------------------------------------------------------------
# Run the arms
# ---------------------------------------------------------------------------
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
        # tckit arm: bench manages MCP per run and /opens the active fixture
        # sln (temp under --isolate-cwd) so the server targets it.
        $tckitExtras = @(
            '--inject-skills', 'plugin/skills',
            '--mcp-cmd',       'uv run python -m tckit.server --transport sse',
            '--mcp-url',       $McpUrl
        )
        Invoke-Arm -Config 'tckit' -ExtraArgs $tckitExtras
    }
    if ($Arm -in 'vanilla','both') {
        Invoke-Arm -Config 'empty' -ExtraArgs @()
    }
}
finally {
    # Close the solution we opened during the run so the next operator
    # cleanup (e.g. git checkout to revert the fixture) does not trigger
    # XAE's "modified externally" popup.
    Write-Host ''
    Write-Host "Closing bridge-loaded solution..."
    try {
        Invoke-RestMethod -Uri "$BridgeUrl/close" -Method Post -Body '{}' `
            -ContentType 'application/json' -TimeoutSec 30 | Out-Null
    } catch {
        Write-Warning "/close failed (non-fatal): $_"
    }
}

Write-Host ''
Write-Host "Done. Results: bench/results/TASK__*__$Task*.json (and .md / .diff / .test-result.json siblings)."
