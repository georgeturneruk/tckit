# Launch the TcKit MCP server from the plugin's repo clone, building it first
# (incremental, so near-instant after the first run). stdout is the MCP
# JSON-RPC channel: only the server may write to it, so all build output is
# routed to stderr.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'dotnet\src\TcKit.Server\TcKit.Server.csproj'
$exe = Join-Path $repoRoot 'dotnet\src\TcKit.Server\bin\Release\net8.0-windows\TcKit.Server.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('TcKit: dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0')
    exit 1
}

dotnet build $project -c Release --nologo -v q | ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('TcKit: server build failed (a .NET 8 SDK is required); see output above.')
    exit $LASTEXITCODE
}

& $exe @args
exit $LASTEXITCODE
