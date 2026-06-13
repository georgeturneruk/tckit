<#
.SYNOPSIS
    Add a new Data Unit Type (struct, enum, or union) under a PLC project's
    DUTs folder, then write its declaration text.

.DESCRIPTION
    DUTs are tree items in their own right and live under the parallel
    DUTs folder (TIPC^<plc>^<plc> Project^DUTs), not under POUs. The
    DutKind discriminator picks the CreateChild kind:

      struct -> 606
      enum   -> 605
      union  -> 607

    DUTs only carry a declaration block (no implementation), so we set
    DeclarationText only.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the new DUT (e.g. ST_Config, E_State).

.PARAMETER DutKind
    One of: struct (default), enum, union.

.PARAMETER Code
    Full ST source text (TYPE Foo : STRUCT ... END_STRUCT END_TYPE, etc.).
#>
param(
    [string]$ProjectPath  = '',
    [string]$PlcName      = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [ValidateSet('struct', 'enum', 'union')][string]$DutKind = 'struct',
    [string]$Code         = '',
    [string]$ParentFolder = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $kind = Get-TcKind -Type $DutKind

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $duts = Get-TcDutsFolder -SysManager $sm -PlcName $plcName
    $parent = Resolve-TcFolderPath -Root $duts -Path $ParentFolder
    $newItem = $parent.CreateChild($Name, $kind, $null, $null)

    if ($Code) {
        Set-TcItemSource -Item $newItem -Declaration $Code
    }
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; kind = $kind; dut_kind = $DutKind; plc = $plcName; parent_folder = $ParentFolder }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
