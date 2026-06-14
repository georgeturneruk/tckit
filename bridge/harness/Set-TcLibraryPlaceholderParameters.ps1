<#
.SYNOPSIS
    Set / update library parameter overrides on an existing
    PlaceholderReference in a consumer PLC project. See ADR-0011.

.DESCRIPTION
    Narrower verb than Add-TcLibraryPlaceholder.ps1: takes the
    placeholder name and the parameter mapping only; default_library /
    version / distributor are not touched. The placeholder must already
    exist (call Add-TcLibraryPlaceholder.ps1 first if it does not).

    Mirrors the close/edit/reopen DTE dance used by
    Add-TcLibraryPlaceholder so the in-memory model is rehydrated from
    disk after the splice and subsequent harness calls in the same
    session see the new overrides.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Consumer PLC project hosting the placeholder. Optional if exactly
    one PLC project exists in the sln. Falls back to PLC_PROJECT_NAME.

.PARAMETER PlaceholderName
    Existing placeholder to target.

.PARAMETER Parameters
    Nested hashtable of parameter overrides, grouped by list name:
    @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }.
    Both list and key names are uppercased on disk; values written
    verbatim (TwinCAT booleans need 'TRUE' / 'FALSE').
#>
param(
    [string]$ProjectPath     = '',
    [string]$PlcName         = $env:PLC_PROJECT_NAME,
    [string]$PlaceholderName,
    [hashtable]$Parameters   = @{},
    [string]$ComVersion      = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode         = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PlaceholderName) { return @{ success = $false; error = 'PlaceholderName required.' } }
    if ($Parameters.Count -eq 0) {
        return @{ success = $false; error = 'Parameters required and must be non-empty.' }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    if (-not $ProjectPath) { $ProjectPath = $dte.Solution.FullName }
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName

    $plcProjPath = Find-TcPlcProjFile -SolutionPath $ProjectPath -PlcName $plc
    $present = Test-TcPlcProjHasPlaceholder `
        -PlcProjPath $plcProjPath `
        -PlaceholderName $PlaceholderName
    if (-not $present) {
        return @{
            success = $false
            error   = "PlaceholderReference '$PlaceholderName' not found in $plcProjPath. Use add_library_placeholder to add it first."
        }
    }

    # Close the solution before the file edit so that the next
    # File.SaveAll on this DTE session can't regenerate the .plcproj
    # from an in-memory tree that doesn't know about our injected
    # overrides. Reopen from disk afterwards so subsequent harness
    # calls see consistent data.
    $dte.Solution.Close($false) | Out-Null
    Set-TcPlcProjPlaceholderParameters `
        -PlcProjPath $plcProjPath `
        -PlaceholderName $PlaceholderName `
        -Parameters $Parameters
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null

    return @{
        success = $true
        details = @{
            consumer_plc = $plc
            placeholder  = $PlaceholderName
            parameters   = $Parameters
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
