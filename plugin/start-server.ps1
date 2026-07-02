# Launch the TcKit MCP server. stdout is the MCP JSON-RPC channel, so ONLY the
# server may write to it; every diagnostic here goes to stderr.
#
# Server resolution order:
#   1. $env:TCKIT_SERVER_EXE  - explicit override / offline pre-placement
#   2. cached prebuilt exe for this plugin version
#   3. build from source, if the .NET 8 SDK is present (the contributor path)
#   4. download the matching self-contained release exe from GitHub, cache it
# If none of those work, print how to fix it and exit non-zero.
$ErrorActionPreference = 'Stop'

function Write-Err { param([string]$Message) [Console]::Error.WriteLine($Message) }

$pluginRoot = $PSScriptRoot
$repoRoot   = (Resolve-Path (Join-Path $pluginRoot '..')).Path

# Version pins which release asset we fetch; read it from the plugin manifest.
$version  = '0.0.0'
$manifest = Join-Path $pluginRoot '.claude-plugin\plugin.json'
if (Test-Path $manifest) {
    try { $version = (Get-Content $manifest -Raw | ConvertFrom-Json).version } catch { }
}

$asset    = 'tckit-server-win-x64.exe'
$cacheDir = Join-Path $env:LOCALAPPDATA "tckit\bin\$version"
$cacheExe = Join-Path $cacheDir $asset

$serverExe = $null

# 1. Explicit override.
if ($env:TCKIT_SERVER_EXE -and (Test-Path $env:TCKIT_SERVER_EXE)) {
    $serverExe = $env:TCKIT_SERVER_EXE
}

# 2. Cached prebuilt for this version.
if (-not $serverExe -and (Test-Path $cacheExe)) {
    $serverExe = $cacheExe
}

# 3. Build from source when the SDK is available (contributors, SDK users).
if (-not $serverExe -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $project = Join-Path $repoRoot 'dotnet\src\TcKit.Server\TcKit.Server.csproj'
    $built   = Join-Path $repoRoot 'dotnet\src\TcKit.Server\bin\Release\net8.0-windows\TcKit.Server.exe'
    if (Test-Path $project) {
        Write-Err 'TcKit: .NET 8 SDK found; building server from source (incremental)...'
        dotnet build $project -c Release --nologo -v q | ForEach-Object { Write-Err $_ }
        if ($LASTEXITCODE -eq 0 -and (Test-Path $built)) {
            $serverExe = $built
        } else {
            Write-Err 'TcKit: build failed; falling back to the prebuilt download.'
        }
    }
}

# 4. Download the matching self-contained release exe (the zero-SDK path).
if (-not $serverExe) {
    $base = "https://github.com/georgeturneruk/tckit/releases/download/v$version"
    Write-Err "TcKit: no local server available; downloading prebuilt v$version (~74 MB, one time)..."
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        New-Item -ItemType Directory -Force $cacheDir | Out-Null
        $tmp = "$cacheExe.download"
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $tmp -UseBasicParsing

        # Verify the checksum when a .sha256 asset is published alongside.
        $expected = $null
        try {
            $expected = ((Invoke-WebRequest -Uri "$base/$asset.sha256" -UseBasicParsing).Content -split '\s+')[0].Trim().ToLower()
        } catch { }
        if ($expected) {
            $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower()
            if ($expected -ne $actual) {
                Remove-Item $tmp -Force
                throw "checksum mismatch (expected $expected, got $actual)"
            }
        }

        Move-Item -Force $tmp $cacheExe
        $serverExe = $cacheExe
        Write-Err "TcKit: cached at $cacheExe"
    } catch {
        Write-Err "TcKit: download failed: $($_.Exception.Message)"
    }
}

if (-not $serverExe) {
    Write-Err ''
    Write-Err 'TcKit: could not obtain the server. Pick one:'
    Write-Err '  - Install the .NET 8 SDK, then relaunch: https://dotnet.microsoft.com/download/dotnet/8.0'
    Write-Err "  - Or download $asset from https://github.com/georgeturneruk/tckit/releases/tag/v$version"
    Write-Err '    and set the TCKIT_SERVER_EXE environment variable to its full path.'
    exit 1
}

& $serverExe @args
exit $LASTEXITCODE
