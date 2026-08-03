# Pack TcKit.Ads as a nupkg and publish it to a local folder feed (ADR-0016).
#
# Consumers point a nuget.config at the feed and pin a version:
#   <packageSources><add key="local" value="C:\nuget-local" /></packageSources>
#   <PackageReference Include="TcKit.Ads" Version="0.1.0" />
#
# The package version comes from <Version> in TcKit.Ads.csproj (SemVer; bump it
# there). Going non-local later is pushing the same nupkg somewhere else.
param(
    [string]$Feed = 'C:\nuget-local'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\TcKit.Ads\TcKit.Ads.csproj'

New-Item -ItemType Directory -Force $Feed | Out-Null
dotnet pack $project -c Release -o $Feed --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }

Get-ChildItem $Feed -Filter 'TcKit.Ads.*.nupkg' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName |
    ForEach-Object { Write-Host "Published $_" }
