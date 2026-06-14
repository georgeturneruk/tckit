<#
.SYNOPSIS
    Delete a property from a POU, defensively removing its Get/Set
    accessor children first.

.DESCRIPTION
    Locates the POU under TIPC^<plc>^<plc> Project^POUs, then deletes
    the property by name. Get/Set accessors are tree-item children of
    the property (kinds 613/614 for FB properties, 654/655 for
    interface properties); whether DeleteChild on the property cascades
    to remove them is undocumented on infosys, so this script removes
    them first when present. The actual order matches what the IDE
    does interactively: kill accessors, then the property body.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the containing POU.

.PARAMETER PropertyName
    Name of the property to delete.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$PropertyName,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PouName)      { return @{ success = $false; error = 'PouName required.' } }
    if (-not $PropertyName) { return @{ success = $false; error = 'PropertyName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    $pou = Find-TcChild -Root $pous -Name $PouName
    if ($null -eq $pou -or $pou.Name -eq $pous.Name) {
        return @{ success = $false; error = "POU '$PouName' not found under POUs of '$plcName'." }
    }

    $property = Find-TcChild -Root $pou -Name $PropertyName
    if ($null -eq $property -or $property.Name -eq $pou.Name) {
        return @{ success = $false; error = "Property '$PropertyName' not found under POU '$PouName'." }
    }

    # Remove Get / Set children first. XAE names them "Get" / "Set" regardless
    # of kind (FB vs interface property), so a name-based DeleteChild covers
    # both branches without us needing to look up the accessor kind.
    $removedAccessors = @()
    foreach ($accessorName in @('Get', 'Set')) {
        $accessor = $null
        try {
            for ($i = 1; $i -le $property.ChildCount; $i++) {
                $child = $property.Child($i)
                if ($child.Name -eq $accessorName) { $accessor = $child; break }
            }
        } catch { $accessor = $null }
        if ($null -ne $accessor) {
            try {
                $property.DeleteChild($accessorName)
                $removedAccessors += $accessorName
            } catch {
                # Some XAE versions cascade-delete on the property; the
                # accessor disappears when we walk the children again. Tolerate
                # silently — the final DeleteChild on the property is what
                # actually matters.
            }
        }
    }

    $pou.DeleteChild($PropertyName)
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; property = $PropertyName; plc = $plcName; removed_accessors = $removedAccessors }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
