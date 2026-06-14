<#
.SYNOPSIS
    Add a new method (or action / property) to an existing POU.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER MethodName
    Name of the new item.

.PARAMETER ItemType
    Kind of item to add. One of: method (default), action, property.

.PARAMETER Code
    Combined declaration + implementation. See Add-TcPou.ps1 for shape.

.PARAMETER Declaration
    Optional explicit declaration (overrides the split of $Code).

.PARAMETER Implementation
    Optional explicit implementation.
#>
param(
    [string]$ProjectPath    = '',
    [string]$PlcName        = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$MethodName,
    [ValidateSet('method', 'action', 'property')][string]$ItemType = 'method',
    [string]$Code           = '',
    [string]$Declaration    = '',
    [string]$Implementation = '',
    [string]$ParentFolder   = '',
    [string]$ComVersion     = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode        = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PouName)     { return @{ success = $false; error = 'PouName required.' } }
    if (-not $MethodName)  { return @{ success = $false; error = 'MethodName required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
    # ParentFolder, when given, is the folder under POUs where the parent
    # POU lives; we use it for a direct lookup so a name collision in
    # another subtree can't win. Empty means "search recursively".
    $pou = $null
    if ($ParentFolder) {
        $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
        $folder = Resolve-TcFolderPath -Root $pous -Path $ParentFolder
        for ($i = 1; $i -le $folder.ChildCount; $i++) {
            $child = $folder.Child($i)
            if ($child.Name -eq $PouName) { $pou = $child; break }
        }
    } else {
        $pou = Find-TcChild -Root $plcProj -Name $PouName
    }
    if ($null -eq $pou) { return @{ success = $false; error = "POU '$PouName' not found." } }

    $kind = Get-TcKind -Type $ItemType
    if ($ItemType -eq 'method' -and (Test-TcInterfacePou -Item $pou)) {
        $kind = Get-TcKind -Type 'interface_method'
    }
    $newItem = $pou.CreateChild($MethodName, $kind, $null, $null)

    if ($Declaration -or $Implementation) {
        Set-TcItemSource -Item $newItem -Declaration $Declaration -Implementation $Implementation
    } elseif ($Code) {
        Set-TcItemSource -Item $newItem -Code $Code
    }
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ pou = $PouName; method = $MethodName; kind = $kind; plc = $plcName }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
