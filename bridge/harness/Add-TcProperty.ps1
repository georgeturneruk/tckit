<#
.SYNOPSIS
    Add a new property (with Get, Set, or both accessors) to an existing POU.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER PouName
    Name of the parent POU.

.PARAMETER PropertyName
    Name of the new property.

.PARAMETER ReturnType
    TwinCAT type the property exposes (e.g. LREAL, BOOL, E_MyEnum). Written
    to the property parent's declaration as ``PROPERTY <name> : <type>``.

.PARAMETER GetterCode
    Optional body of the Get accessor. May include a local VAR block. The
    bridge splits at the last END_VAR (Split-TcCode), or treats the whole
    string as the implementation when no VAR block is present. Empty string
    or absent: no Get accessor is created.

.PARAMETER SetterCode
    Optional body of the Set accessor. Same shape as GetterCode. Empty
    string or absent: no Set accessor is created.

    At least one of GetterCode or SetterCode must be supplied.
#>
param(
    [string]$ProjectPath  = '',
    [string]$PlcName      = $env:PLC_PROJECT_NAME,
    [string]$PouName,
    [string]$PropertyName,
    [string]$ReturnType,
    [string]$GetterCode   = '',
    [string]$SetterCode   = '',
    [string]$ParentFolder = '',
    [string]$ComVersion   = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode      = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

try {
    if (-not $PouName)        { return @{ success = $false; error = 'PouName required.' } }
    if (-not $PropertyName)   { return @{ success = $false; error = 'PropertyName required.' } }
    if (-not $ReturnType)     { return @{ success = $false; error = 'ReturnType required.' } }
    if (-not $GetterCode -and -not $SetterCode) {
        return @{
            success = $false
            error   = 'add_property requires at least one of GetterCode or SetterCode.'
        }
    }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $plcProj = Get-TcPlcProjectNode -SysManager $sm -PlcName $plcName
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

    # Properties under an INTERFACE need different CreateChild kinds (612 /
    # 654 / 655) and a different vInfo shape than properties under an FB.
    # Mirrors the InterfaceMethod branch in Add-TcMethod.ps1.
    #
    # vInfo shape (see Beckhoff samples, TC_AI_DOTNET_Samples
    # GeneratePlcProject.cs:1106-1119 for FB, :213-216 for interface):
    #
    #   FB property parent:   [language, return_type, access_modifier]
    #   FB property Get/Set:  [language, access_modifier, body_xml_seed]
    #   ITF property parent:  return_type as a single string
    #   ITF property Get/Set: $null
    #
    # Set-TcItemSource overwrites the seed body straight after, so the body
    # XML in vInfo[2] is only the initial placeholder XAE generates while
    # the tree item is being constructed.
    $isInterface = Test-TcInterfacePou -Item $pou
    if ($isInterface) {
        $kindProperty = Get-TcKind -Type 'interface_property'
        $kindGet      = Get-TcKind -Type 'interface_property_get'
        $kindSet      = Get-TcKind -Type 'interface_property_set'
        $propertyVInfo = [object]$ReturnType
        $getVInfo      = $null
        $setVInfo      = $null
    } else {
        $kindProperty = Get-TcKind -Type 'property'
        $kindGet      = Get-TcKind -Type 'property_get'
        $kindSet      = Get-TcKind -Type 'property_set'
        $propertyVInfo = [string[]]@('ST', $ReturnType, 'PUBLIC')
        $getVInfo      = [string[]]@('ST', 'PUBLIC', '<ST><![CDATA[(* ST PropGet *)]]></ST>')
        $setVInfo      = [string[]]@('ST', 'PUBLIC', '<ST><![CDATA[(* ST PropSet *)]]></ST>')
    }

    # 3rd arg (bstrBefore) is $null — insert at end. 4th arg (vInfo) shape
    # is the load-bearing thing: passing $null or a scalar string here is
    # what previously caused the 'Object reference not set' / 'Requested
    # value LREAL was not found' errors.
    $null = $pou.CreateChild($PropertyName, $kindProperty, $null, $propertyVInfo)

    # Build the LookupTreeItem path for the new property so we can fetch a
    # fresh reference for each subsequent CreateChild. PowerShell's
    # apartment-threaded COM marshalling does not preserve tree-item refs
    # across mutating calls reliably — re-using the parent ref between the
    # Get and Set CreateChild calls surfaces "Item 'Get' is deleted or
    # invalidated by an earlier operation!" on XAE versions that revalidate
    # siblings during accessor creation. LookupTreeItem on the full path
    # gives us a stable single-call reference.
    $folderSegments = if ($ParentFolder) { ($ParentFolder -split '[/\\]') | Where-Object { $_ } } else { @() }
    $pathSegments = @("TIPC", $plcName, "$plcName Project", 'POUs') + $folderSegments + @($PouName, $PropertyName)
    $propPath = $pathSegments -join '^'

    $accessors = @()
    $getItem = $null
    $setItem = $null

    # Accessor name is intentionally empty: the kind constant (613/614/654/655)
    # already identifies which accessor this is, and XAE names the child item
    # itself.
    if ($GetterCode) {
        $propParent = $sm.LookupTreeItem($propPath)
        $getItem = $propParent.CreateChild('', $kindGet, $null, $getVInfo)
        Set-TcItemSource -Item $getItem -Code $GetterCode
        Save-TcSolution -Dte $dte  # flush tree mutations before adding sibling
        $accessors += 'Get'
    }
    if ($SetterCode) {
        $propParent = $sm.LookupTreeItem($propPath)
        $setItem = $propParent.CreateChild('', $kindSet, $null, $setVInfo)
        Set-TcItemSource -Item $setItem -Code $SetterCode
        $accessors += 'Set'
    }

    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{
            pou         = $PouName
            property    = $PropertyName
            return_type = $ReturnType
            accessors   = $accessors
            plc         = $plcName
        }
    }
}
catch {
    return @{ success = $false; error = $_.Exception.Message }
}
