#Requires -Version 5.1
<#
.SYNOPSIS
    TcKit Windows bridge service — REST API gateway to TwinCAT XAE.

.DESCRIPTION
    Listens on http://localhost:8765 and routes requests to PowerShell harness scripts.
    The Docker container calls this bridge for anything that requires COM/XAE.

    Routes:
      POST /build              -> harness\Invoke-TcBuild.ps1
      POST /deploy             -> harness\Invoke-TcDeploy.ps1
      POST /runtime            -> harness\Invoke-TcRuntime.ps1
      POST /tcunit-run         -> harness\Invoke-TcUnitRun.ps1
      POST /open               -> harness\Open-TcProject.ps1
      POST /create             -> harness\New-TcProject.ps1
      POST /pou                -> harness\Add-TcPou.ps1
      POST /method             -> harness\Add-TcMethod.ps1
      POST /item               -> harness\Update-TcPouItem.ps1
      POST /item-patch         -> harness\Update-TcPouItemPatch.ps1
      POST /add-variable       -> harness\Add-TcVariable.ps1
      POST /results            -> harness\Get-TcUnitResults.ps1
      POST /install-dependency -> Install-Module (allow-listed modules only)
      GET  /health             -> {"status": "ok", "dependencies": {...}}

.PARAMETER Port
    Port to listen on. Default: 8765.

.EXAMPLE
    .\Start-Bridge.ps1
    .\Start-Bridge.ps1 -Port 9000
#>
param(
    [int]$Port = 8765
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HarnessDir = Join-Path $PSScriptRoot 'harness'

# Allow-list for /install-dependency. Only modules TcKit itself surfaces via
# the doctor are installable through the bridge. Pinned to the same minimum
# version the harness scripts require.
$InstallableDependencies = @{
    'TcXaeMgmt' = @{ MinimumVersion = '6.0' }
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$ts] [$Level] $Message"
}

function Send-JsonResponse {
    param(
        [System.Net.HttpListenerResponse]$Response,
        [object]$Body,
        [int]$StatusCode = 200
    )
    $json = $Body | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Response.StatusCode = $StatusCode
    $Response.ContentType = 'application/json; charset=utf-8'
    $Response.ContentLength64 = $bytes.Length
    $Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Response.OutputStream.Close()
}

function ConvertTo-HashtableDeep {
    <#
    .SYNOPSIS
        Recursively convert a PSCustomObject (or array of them) into a
        hashtable so the result can be splatted at a harness script.
    .DESCRIPTION
        ConvertFrom-Json returns a PSCustomObject on Windows PowerShell 5.1.
        Splatting requires a hashtable, so we walk the object graph and
        build one. The -AsHashtable switch on ConvertFrom-Json exists from
        PowerShell 7 onwards; keeping this manual conversion preserves
        5.1 compatibility.
    #>
    param($Object)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { return $Object }
    if ($Object -is [System.Management.Automation.PSCustomObject]) {
        $ht = @{}
        foreach ($prop in $Object.PSObject.Properties) {
            $ht[$prop.Name] = ConvertTo-HashtableDeep -Object $prop.Value
        }
        return $ht
    }
    if ($Object -is [System.Collections.IEnumerable] -and $Object -isnot [string]) {
        return @($Object | ForEach-Object { ConvertTo-HashtableDeep -Object $_ })
    }
    return $Object
}

function Read-RequestBody {
    param([System.Net.HttpListenerRequest]$Request)
    if ($Request.ContentLength64 -le 0) { return @{} }
    $reader = New-Object System.IO.StreamReader($Request.InputStream)
    $raw = $reader.ReadToEnd()
    $reader.Close()
    try {
        $parsed = $raw | ConvertFrom-Json
    }
    catch {
        return @{}
    }
    $ht = ConvertTo-HashtableDeep -Object $parsed
    if ($ht -is [System.Collections.IDictionary]) { return $ht }
    return @{}
}

function Get-BridgeDependencies {
    <#
    .SYNOPSIS
        Report the installed version of each bridge dependency, or $null
        if the module isn't available on the current PSModulePath.
    #>
    $deps = @{}
    foreach ($name in $InstallableDependencies.Keys) {
        $mod = Get-Module -ListAvailable $name -ErrorAction SilentlyContinue |
            Sort-Object Version -Descending | Select-Object -First 1
        $deps[$name] = if ($mod) { $mod.Version.ToString() } else { $null }
    }
    return $deps
}

function Install-BridgeDependency {
    <#
    .SYNOPSIS
        Install one allow-listed module from PSGallery into CurrentUser
        scope. Returns the resulting version on success.
    #>
    param([string]$Name)

    if (-not $InstallableDependencies.ContainsKey($Name)) {
        return @{ success = $false; error = "Module '$Name' is not in the bridge install allow-list." }
    }
    $spec = $InstallableDependencies[$Name]
    try {
        Install-Module -Name $Name `
                       -Scope CurrentUser `
                       -MinimumVersion $spec.MinimumVersion `
                       -Force `
                       -AcceptLicense `
                       -ErrorAction Stop
    } catch {
        return @{ success = $false; error = "Install-Module $Name failed: $($_.Exception.Message)" }
    }
    $mod = Get-Module -ListAvailable $Name -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending | Select-Object -First 1
    return @{
        success = $true
        details = @{
            name    = $Name
            version = if ($mod) { $mod.Version.ToString() } else { $null }
            scope   = 'CurrentUser'
        }
    }
}

function Invoke-Harness {
    param([string]$Script, [hashtable]$Params = @{})
    $scriptPath = Join-Path $HarnessDir $Script
    if (-not (Test-Path $scriptPath)) {
        return @{ success = $false; error = "Harness script not found: $Script" }
    }
    try {
        $result = & $scriptPath @Params
        return $result
    }
    catch {
        return @{ success = $false; error = $_.Exception.Message }
    }
}

# ------------------------------------------------------------------
# Start listener
# ------------------------------------------------------------------

$prefix = "http://localhost:$Port/"
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)
$listener.Start()

Write-Log "TcKit bridge listening on $prefix"
Write-Log "Press Ctrl+C to stop."

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $req = $context.Request
        $res = $context.Response

        $method = $req.HttpMethod.ToUpper()
        $path   = $req.Url.AbsolutePath.TrimEnd('/')

        Write-Log "$method $path"

        try {
            switch ("$method $path") {
                'GET /health' {
                    Send-JsonResponse -Response $res -Body @{
                        status       = 'ok'
                        version      = '0.1.0'
                        dependencies = Get-BridgeDependencies
                    }
                }
                'POST /install-dependency' {
                    $body   = Read-RequestBody -Request $req
                    $name   = if ($body.ContainsKey('name')) { [string]$body['name'] } else { '' }
                    if (-not $name) {
                        Send-JsonResponse -Response $res -Body @{ success = $false; error = "'name' field required." } -StatusCode 400
                    } else {
                        $result = Install-BridgeDependency -Name $name
                        Send-JsonResponse -Response $res -Body $result
                    }
                }
                'POST /build' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Invoke-TcBuild.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /deploy' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Invoke-TcDeploy.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /runtime' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Invoke-TcRuntime.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /tcunit-run' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Invoke-TcUnitRun.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /open' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Open-TcProject.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /create' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'New-TcProject.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /pou' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Add-TcPou.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /method' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Add-TcMethod.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /item' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Update-TcPouItem.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /item-patch' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Update-TcPouItemPatch.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /add-variable' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Add-TcVariable.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                'POST /results' {
                    $body   = Read-RequestBody -Request $req
                    $result = Invoke-Harness -Script 'Get-TcUnitResults.ps1' -Params $body
                    Send-JsonResponse -Response $res -Body $result
                }
                default {
                    Send-JsonResponse -Response $res -Body @{ error = 'Not found' } -StatusCode 404
                }
            }
        }
        catch {
            Write-Log "Error handling $method $path`: $_" -Level 'ERROR'
            try {
                Send-JsonResponse -Response $res -Body @{ error = $_.Exception.Message } -StatusCode 500
            }
            catch { }
        }
    }
}
finally {
    $listener.Stop()
    Write-Log 'Bridge stopped.'
}
