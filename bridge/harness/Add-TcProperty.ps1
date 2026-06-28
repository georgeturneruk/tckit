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

    # Declared up front so the partial-failure cleanup in catch can reference
    # them even when we throw before they're assigned (Set-StrictMode is on).
    # NB: do not init $plcName here — PowerShell variable names are
    # case-insensitive, so it aliases the $PlcName parameter; nulling it would
    # wipe the caller's explicit PLC name. The cleanup only runs once $sm is
    # set, by which point $plcName has been resolved.
    $dte = $null; $sm = $null
    # Tracks whether THIS call created the property parent. Cleanup on failure
    # must only remove a property we created — never one that already existed
    # (a re-add hits "already exists" on the parent CreateChild before this
    # flips true, so the pre-existing property is left untouched).
    $propCreated = $false

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
    # 654 / 655) and a different parent vInfo shape than properties under an FB.
    # Mirrors the InterfaceMethod branch in Add-TcMethod.ps1.
    #
    # Parent vInfo (see Beckhoff samples, TC_AI_DOTNET_Samples
    # GeneratePlcProject.cs:1106-1119 for FB, :213-216 for interface):
    #
    #   FB property parent:   [language, return_type, access_modifier]
    #   ITF property parent:  return_type as a single string
    #
    # Accessor vInfo is $null for BOTH kinds. An FB accessor used to be seeded
    # with [language, access_modifier, body_xml_seed], but that seed array makes
    # CreateChild fail with "Element not found!" when the property's POU lives in
    # a SUBFOLDER (e.g. POUs/RingBuffer) — the parent is created, then the Get
    # CreateChild throws, leaving an orphan. Passing $null (as the interface
    # branch always has) creates the accessor with XAE's default PUBLIC / VAR..
    # END_VAR declaration; Set-TcItemSource then writes the real body, which is
    # what overwrote the seed anyway. So the seed bought nothing and cost the
    # subfolder case.
    $isInterface = Test-TcInterfacePou -Item $pou
    if ($isInterface) {
        $kindProperty = Get-TcKind -Type 'interface_property'
        $kindGet      = Get-TcKind -Type 'interface_property_get'
        $kindSet      = Get-TcKind -Type 'interface_property_set'
        $propertyVInfo = [object]$ReturnType
    } else {
        $kindProperty = Get-TcKind -Type 'property'
        $kindGet      = Get-TcKind -Type 'property_get'
        $kindSet      = Get-TcKind -Type 'property_set'
        $propertyVInfo = [string[]]@('ST', $ReturnType, 'PUBLIC')
    }
    $getVInfo = $null
    $setVInfo = $null

    # 3rd arg (bstrBefore) is $null — insert at end. 4th arg (vInfo) shape
    # is the load-bearing thing: passing $null or a scalar string here is
    # what previously caused the 'Object reference not set' / 'Requested
    # value LREAL was not found' errors. Keep the returned property item — the
    # interface branch creates its accessors directly on it.
    $propItem = $pou.CreateChild($PropertyName, $kindProperty, $null, $propertyVInfo)
    $propCreated = $true

    $accessors = @()

    if ($isInterface) {
        # Interface property accessors have no implementation body. Mirror the
        # Beckhoff sample (TC_AI_DOTNET_Samples GeneratePlcProject.cs,
        # AddProperty): create Get/Set directly on the property item with
        # vInfo=$null, no Set-TcItemSource, and no Save between them. The
        # accessor's declaration (PUBLIC / VAR..END_VAR) is created by XAE;
        # GetterCode/SetterCode only signal *which* accessors to add — their
        # content is ignored for interfaces.
        #
        # The FB branch below re-finds the parent via LookupTreeItem and Saves
        # between accessors to dodge a stale-parent ref left behind by
        # Set-TcItemSource writing a body. Applied to an interface that dance
        # CRASHES TcXaeShell on the second CreateChild (surfacing as RPC
        # 0x800706BE): there is no body to write, and the mid-operation Save +
        # re-lookup hands CreateChild a property ref XAE faults on. Reusing the
        # in-process $propItem ref with no mutation between the two creates is
        # both correct per the sample and crash-free.
        if ($GetterCode) { $null = $propItem.CreateChild('', $kindGet, $null, $null); $accessors += 'Get' }
        if ($SetterCode) { $null = $propItem.CreateChild('', $kindSet, $null, $null); $accessors += 'Set' }
    }
    else {
        # FB property: accessors carry bodies. Create both Get/Set directly on
        # the in-process property item (as the interface branch does), capturing
        # each child ref, THEN write the bodies with Set-TcItemSource. This
        # replaces the former Save + LookupTreeItem "dance" between accessors.
        #
        # The dance, plus a body-seed accessor vInfo, was what broke properties
        # on an FB in a SUBFOLDER: the property parent created fine, then the Get
        # CreateChild threw "Element not found!" and left an orphan. Two changes
        # fix it: accessor vInfo is now $null (see the parent/accessor vInfo
        # comment above — the seed was the actual trigger), and we no longer
        # re-resolve the parent via LookupTreeItem (the in-process $propItem ref
        # works at any folder depth). Creating both children before any
        # Set-TcItemSource also avoids the "Item 'Get' is deleted or
        # invalidated..." stale-ref the dance originally guarded against.
        #
        # Accessor name is intentionally empty: the kind constant (613/614)
        # already identifies which accessor this is, and XAE names the child.
        $getItem = $null
        $setItem = $null
        if ($GetterCode) { $getItem = $propItem.CreateChild('', $kindGet, $null, $getVInfo); $accessors += 'Get' }
        if ($SetterCode) { $setItem = $propItem.CreateChild('', $kindSet, $null, $setVInfo); $accessors += 'Set' }
        if ($null -ne $getItem) { Set-TcItemSource -Item $getItem -Code $GetterCode }
        if ($null -ne $setItem) { Set-TcItemSource -Item $setItem -Code $SetterCode }
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
    $err = $_.Exception.Message
    # A CreateChild / Set-TcItemSource failure can leave a partially created
    # property (parent node, maybe a Get accessor) behind. Remove it so the
    # operation stays idempotent — a retry must not hit "already exists".
    # Re-resolve the POU freshly rather than trusting a possibly-stale ref.
    try {
        if ($propCreated -and $null -ne $sm -and $PouName -and $PropertyName) {
            $pousCleanup = Get-TcPousFolder -SysManager $sm -PlcName $plcName
            $pouCleanup = Find-TcChild -Root $pousCleanup -Name $PouName
            if ($null -ne $pouCleanup -and $pouCleanup.Name -ne $pousCleanup.Name) {
                $null = Remove-TcPropertyNode -Pou $pouCleanup -PropertyName $PropertyName -BestEffort
                Save-TcSolution -Dte $dte
            }
        }
    } catch { }
    return @{ success = $false; error = $err }
}
