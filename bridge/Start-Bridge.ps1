#Requires -Version 5.1
<#
.SYNOPSIS
    TcKit Windows bridge service — REST API gateway to TwinCAT XAE.

.DESCRIPTION
    Listens on http://localhost:8765 and routes requests to PowerShell harness scripts.
    The Docker container calls this bridge for anything that requires COM/XAE.

    Routes:
      POST /build    -> harness\Invoke-TcBuild.ps1
      POST /deploy   -> harness\Invoke-TcDeploy.ps1
      POST /runtime  -> harness\Invoke-TcRuntime.ps1
      POST /open     -> harness\Open-TcProject.ps1
      POST /create   -> harness\New-TcProject.ps1
      POST /pou      -> harness\Add-TcPou.ps1
      POST /method   -> harness\Add-TcMethod.ps1
      POST /item     -> harness\Update-TcPouItem.ps1
      GET  /results  -> harness\Get-TcUnitResults.ps1
      GET  /health   -> {"status": "ok"}

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

function Read-RequestBody {
    param([System.Net.HttpListenerRequest]$Request)
    if ($Request.ContentLength64 -le 0) { return @{} }
    $reader = New-Object System.IO.StreamReader($Request.InputStream)
    $raw = $reader.ReadToEnd()
    $reader.Close()
    try { return $raw | ConvertFrom-Json -AsHashtable } catch { return @{} }
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
                    Send-JsonResponse -Response $res -Body @{ status = 'ok'; version = '0.1.0' }
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
                'GET /results' {
                    $result = Invoke-Harness -Script 'Get-TcUnitResults.ps1'
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
