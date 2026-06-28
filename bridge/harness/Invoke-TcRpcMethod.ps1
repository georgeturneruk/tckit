#Requires -Modules @{ ModuleName='TcXaeMgmt'; ModuleVersion='6.0' }
<#
.SYNOPSIS
    Invoke a PLC method decorated with {attribute 'TcRpcEnable'} via ADS.

.DESCRIPTION
    Imports TcXaeMgmt (which loads TwinCAT.Ads into the session), then
    uses TcAdsClient.InvokeRpcMethod to call the decorated method.

    The PLC method must carry {attribute 'TcRpcEnable'} in its declaration.
    Parameters are positional and must match the method's VAR_INPUT order.
    The return value (if any) is serialised to a string and returned as
    details.return_value; details.return_type carries the .NET type name.

.PARAMETER TargetAmsId
    AMS Net ID of the target. Falls back to TARGET_AMS_ID env var.

.PARAMETER SymbolPath
    Instance path of the FB owning the method (e.g. "MAIN.fbPid").
    Use "MAIN" for methods on the MAIN program directly.

.PARAMETER MethodName
    Method name as declared in the PLC (e.g. "M_Reset").

.PARAMETER ParamsJson
    JSON array of positional parameters matching the method's VAR_INPUT
    order. Double-encoded to preserve types through PS 5.1 JSON decode
    (e.g. '[42, true, "hello"]'). Defaults to '[]'.

.PARAMETER Port
    PLC runtime port. Default 851 (the standard first-PLC port).
#>
param(
    [string]$TargetAmsId = $env:TARGET_AMS_ID,
    [string]$SymbolPath  = '',
    [string]$MethodName  = '',
    [string]$ParamsJson  = '[]',
    [int]   $Port        = 851
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# TcXaeMgmt loads TwinCAT.Ads.dll into the PowerShell session; TcAdsClient
# is available as a .NET type after the import.
Import-Module TcXaeMgmt -MinimumVersion 6.0 -ErrorAction Stop

try {
    if (-not $TargetAmsId) { return @{ success = $false; error = 'TargetAmsId required.' } }
    if (-not $SymbolPath)  { return @{ success = $false; error = 'SymbolPath required.' } }
    if (-not $MethodName)  { return @{ success = $false; error = 'MethodName required.' } }

    $params = @($ParamsJson | ConvertFrom-Json)

    $client = New-Object TwinCAT.Ads.TcAdsClient
    try {
        $client.Connect($TargetAmsId, $Port)
        $rawResult = $client.InvokeRpcMethod($SymbolPath, $MethodName, [object[]]$params)
    }
    finally {
        $client.Dispose()
    }

    $out = @{ success = $true }
    if ($null -ne $rawResult) {
        $out['return_value'] = [string]$rawResult
        $out['return_type']  = $rawResult.GetType().Name
    }
    return $out
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
