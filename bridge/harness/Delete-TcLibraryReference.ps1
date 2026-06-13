<#
.SYNOPSIS
    Remove a library reference from a consumer PLC project.

.DESCRIPTION
    Wraps ITcPlcLibraryManager.RemoveReference(name, version, distributor),
    the symmetric counterpart to AddLibrary. The library manager is the
    References tree node at 'TIPC^<plc>^<plc> Project^References'.
    SaveAll persists the change to the consumer .plcproj.

    See https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242888843.html
    for the canonical signature and Beckhoff/TC_AI_DOTNET_Samples'
    ManagePlcLibraries.cs for the lifecycle pattern.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Consumer PLC project. Optional if exactly one PLC project is present.
    Falls back to PLC_PROJECT_NAME.

.PARAMETER LibraryName
    Library name as referenced.

.PARAMETER Version
    Library version as referenced; '*' (default) targets the latest /
    wildcard reference.

.PARAMETER Distributor
    Library distributor / company string. Defaults to 'Tc3 Project'
    matching Add-TcLibraryReference.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$LibraryName,
    [string]$Version     = '*',
    [string]$Distributor = 'Tc3 Project',
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE)    { $env:XAE_MODE }    else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $LibraryName) { return @{ success = $false; error = 'LibraryName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plc = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plc

    $libManager = $sm.LookupTreeItem("TIPC^$plc^$plc Project^References")

    # AddLibrary called with version "*" stays as "*" in COM's declared
    # Version, but RemoveReference's 3-arg form only matches against the
    # resolved EffectiveVersion (e.g. "1.0.0.0"). When the caller passes
    # "*", enumerate the References children and pick the entry whose
    # LibraryName + Distributor match, then read EffectiveVersion from
    # its ProduceXml. Reference children expose no typed accessors for
    # these fields, so the XML round-trip is the only documented path.
    $resolvedVersion = $Version
    if ($Version -eq '*' -or [string]::IsNullOrEmpty($Version)) {
        $resolvedVersion = $null
        for ($i = 1; $i -le $libManager.ChildCount; $i++) {
            $ref = $libManager.Child($i)
            if ($ref.Name -ne $LibraryName) { continue }
            try {
                [xml]$refDoc = $ref.ProduceXml($false)
            } catch {
                continue
            }
            $libNode = $refDoc.SelectSingleNode('//Library')
            if ($null -eq $libNode) { continue }
            $refDist = ''
            $distNode = $libNode.SelectSingleNode('Distributor')
            if ($null -ne $distNode) { $refDist = [string]$distNode.InnerText }
            if ($refDist -ne $Distributor) { continue }
            $effNode = $libNode.SelectSingleNode('EffectiveVersion')
            if ($null -ne $effNode) {
                $resolvedVersion = [string]$effNode.InnerText
            }
            break
        }
        if ($null -eq $resolvedVersion -or $resolvedVersion -eq '') {
            return @{
                success = $false
                error = "No library reference matching name='$LibraryName' distributor='$Distributor' (with a resolved EffectiveVersion) found on '$plc'."
            }
        }
    }

    $libManager.RemoveReference($LibraryName, $resolvedVersion, $Distributor) | Out-Null
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            consumer_plc = $plc
            library      = $LibraryName
            version      = $resolvedVersion
            distributor  = $Distributor
        }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
