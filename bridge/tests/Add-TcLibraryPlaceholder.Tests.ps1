#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Unit tests for the .plcproj-side helpers Add-TcLibraryPlaceholder.ps1
    uses to land library parameter overrides:
      - Set-TcPlcProjPlaceholderParameters (XML splice)
      - Find-TcPlcProjFile (filesystem lookup)

.DESCRIPTION
    The DTE half of Add-TcLibraryPlaceholder.ps1 (AddPlaceholder + the
    Save/Close/Open dance) needs a live XAE attach; these tests pin the
    file-only behaviour so the previous silent-drop bugs can't regress.

    History: the first cut of library-parameter overrides round-tripped
    the placeholder tree item's XML through ProduceXml/ConsumeXml; the
    second cut wrote a <ParameterValues>/<Parameter Name=> shape to
    disk. Neither matched what XAE itself writes. The actual on-disk
    schema is <Parameters>/<Parameter ListName=> with xmlns="" reset on
    the inner element, uppercased ListName/Key, and <Key>/<Value>
    children — see ADR-0007 status notes 2026-05-15.

    Pester 5 quirks worked around here:
      - It names avoid angle brackets; Pester 5's reporter mangles them
        out of test names (and PowerShell parses the resulting string
        as if it had $-substitutions).
      - Helpers are defined inside BeforeAll so they reach the test
        container's script scope.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcDte.psm1') -Force

    $script:MsbuildNs = 'http://schemas.microsoft.com/developer/msbuild/2003'

    $script:SampleSkeleton = @"
<?xml version="1.0" encoding="utf-8"?>
<Project DefaultTargets="Build" xmlns="$script:MsbuildNs">
  <ItemGroup>
    <PlaceholderReference Include="Tc2_System">
      <DefaultResolution>Tc2_System, * (Beckhoff Automation GmbH)</DefaultResolution>
      <Namespace>Tc2_System</Namespace>
    </PlaceholderReference>
    <PlaceholderReference Include="TcUnit">
      <DefaultResolution>TcUnit, * (www.tcunit.org)</DefaultResolution>
      <Namespace>TcUnit</Namespace>
    </PlaceholderReference>
  </ItemGroup>
</Project>
"@

    function script:New-TempPlcProj {
        param([string]$Content)
        $path = Join-Path ([IO.Path]::GetTempPath()) ("placeholder-test-{0}.plcproj" -f ([Guid]::NewGuid()))
        Set-Content -LiteralPath $path -Value $Content -Encoding UTF8
        return $path
    }

    function script:Read-PlcProjXml {
        param([string]$Path)
        [xml]$doc = Get-Content -LiteralPath $Path -Raw
        return $doc
    }

    function script:Get-MsbuildNsMgr {
        param([xml]$Doc)
        $mgr = New-Object System.Xml.XmlNamespaceManager($Doc.NameTable)
        [void]$mgr.AddNamespace('m', $script:MsbuildNs)
        return ,$mgr
    }

    function script:Get-PlaceholderNode {
        param([xml]$Doc, [string]$Name)
        $nsMgr = Get-MsbuildNsMgr -Doc $Doc
        return $Doc.SelectSingleNode("//m:PlaceholderReference[@Include='$Name']", $nsMgr)
    }

    function script:Get-ParameterChildren {
        param($WrapperNode)
        # Unary comma keeps a one-element array from unrolling into a
        # scalar across the function-return pipeline boundary — without
        # it $params.Count would be $null for the single-Parameter case.
        $children = @($WrapperNode.ChildNodes |
            Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and $_.LocalName -eq 'Parameter' })
        return ,$children
    }
}

Describe 'Set-TcPlcProjPlaceholderParameters' {
    BeforeEach {
        $script:tempFile = New-TempPlcProj -Content $script:SampleSkeleton
    }

    AfterEach {
        Remove-Item -LiteralPath $script:tempFile -Force -ErrorAction SilentlyContinue
    }

    It 'splices the wrapper with one Parameter ListName child per key' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $placeholder = Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit'
        $wrapper = $placeholder.SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $wrapper | Should -Not -BeNullOrEmpty
        $params = Get-ParameterChildren -WrapperNode $wrapper
        $params.Count | Should -Be 1
        $params[0].GetAttribute('ListName') | Should -Be 'GVL_PARAM_TCUNIT'
        $key = $params[0].ChildNodes | Where-Object { $_.LocalName -eq 'Key' }
        $value = $params[0].ChildNodes | Where-Object { $_.LocalName -eq 'Value' }
        $key.InnerText | Should -Be 'XUNITENABLEPUBLISH'
        $value.InnerText | Should -Be 'TRUE'
    }

    It 'uppercases both ListName and Key but writes Value verbatim' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitFilePath' = '%TC_BOOTPRJPATH%CustomResults.xml' } }

        $raw = Get-Content -LiteralPath $script:tempFile -Raw
        $raw | Should -Match 'ListName="GVL_PARAM_TCUNIT"'
        $raw | Should -Match '<Key>XUNITFILEPATH</Key>'
        $raw | Should -Match '<Value>%TC_BOOTPRJPATH%CustomResults\.xml</Value>'
    }

    It 'puts the wrapper in the MSBuild xmlns and resets each Parameter to the empty xmlns' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $placeholder = Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit'
        $wrapper = $placeholder.SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $wrapper.NamespaceURI | Should -Be $script:MsbuildNs

        $param = (Get-ParameterChildren -WrapperNode $wrapper)[0]
        $param.NamespaceURI | Should -Be ''
        ($param.ChildNodes | Where-Object { $_.LocalName -eq 'Key' }).NamespaceURI   | Should -Be ''
        ($param.ChildNodes | Where-Object { $_.LocalName -eq 'Value' }).NamespaceURI | Should -Be ''

        # The serialised file must carry an explicit xmlns="" on each
        # Parameter element so XAE re-reads the override from disk.
        (Get-Content -LiteralPath $script:tempFile -Raw) | Should -Match '<Parameter\s+[^>]*xmlns=""'
    }

    It 'leaves sibling PlaceholderReferences untouched' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $sibling = Get-PlaceholderNode -Doc $reloaded -Name 'Tc2_System'
        $sibling.SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded)) | Should -BeNullOrEmpty
    }

    It 'replaces an existing ListName+Key entry rather than duplicating it' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'FALSE' } }
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $wrapper = (Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit').SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $params = Get-ParameterChildren -WrapperNode $wrapper
        $params.Count | Should -Be 1
        ($params[0].ChildNodes | Where-Object { $_.LocalName -eq 'Value' }).InnerText | Should -Be 'TRUE'
    }

    It 'appends additional keys for the same list as additional Parameter siblings' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitFilePath' = '%TC_BOOTPRJPATH%out.xml' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $wrapper = (Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit').SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $params = Get-ParameterChildren -WrapperNode $wrapper
        $params.Count | Should -Be 2
        $keys = $params | ForEach-Object { ($_.ChildNodes | Where-Object { $_.LocalName -eq 'Key' }).InnerText }
        $keys | Should -Contain 'XUNITENABLEPUBLISH'
        $keys | Should -Contain 'XUNITFILEPATH'
    }

    It 'supports keys from more than one parameter list under the same wrapper' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{
                'GVL_Param_TcUnit'       = @{ 'xUnitEnablePublish' = 'TRUE' }
                'GVL_Param_TcUnit_Extra' = @{ 'someOtherKey'       = '42' }
            }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $wrapper = (Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit').SelectSingleNode('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $params = Get-ParameterChildren -WrapperNode $wrapper
        $params.Count | Should -Be 2
        $listNames = $params | ForEach-Object { $_.GetAttribute('ListName') }
        $listNames | Should -Contain 'GVL_PARAM_TCUNIT'
        $listNames | Should -Contain 'GVL_PARAM_TCUNIT_EXTRA'
    }

    It 'reuses the existing wrapper rather than duplicating it' {
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitEnablePublish' = 'TRUE' } }
        Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = @{ 'xUnitFilePath' = '%TC_BOOTPRJPATH%out.xml' } }

        $reloaded = Read-PlcProjXml -Path $script:tempFile
        $placeholder = Get-PlaceholderNode -Doc $reloaded -Name 'TcUnit'
        $wrappers = $placeholder.SelectNodes('m:Parameters', (Get-MsbuildNsMgr -Doc $reloaded))
        $wrappers.Count | Should -Be 1
    }

    It 'throws when the PlaceholderReference does not exist in the file' {
        { Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'NotThere' `
            -Parameters @{ 'List' = @{ 'Foo' = 'Bar' } } } | Should -Throw '*NotThere*'
    }

    It 'throws when the .plcproj path does not exist' {
        { Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath 'C:\nonexistent\nope.plcproj' `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'List' = @{ 'Foo' = 'Bar' } } } | Should -Throw '*not found*'
    }

    It 'throws when an entry in Parameters is not a nested hashtable' {
        { Set-TcPlcProjPlaceholderParameters `
            -PlcProjPath $script:tempFile `
            -PlaceholderName 'TcUnit' `
            -Parameters @{ 'GVL_Param_TcUnit' = 'TRUE' } } | Should -Throw '*hashtable*'
    }
}

Describe 'Find-TcPlcProjFile' {
    BeforeEach {
        $script:tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("findplcproj-{0}" -f ([Guid]::NewGuid()))
        New-Item -ItemType Directory -Path $script:tempRoot -Force | Out-Null
        $script:slnPath = Join-Path $script:tempRoot 'Sample.sln'
        Set-Content -LiteralPath $script:slnPath -Value '' -Encoding UTF8
    }

    AfterEach {
        Remove-Item -LiteralPath $script:tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'finds the .plcproj for a PLC nested inside a TwinCAT wrapper folder' {
        $plcDir = Join-Path $script:tempRoot 'MySln_Tc\MyPlc'
        New-Item -ItemType Directory -Path $plcDir -Force | Out-Null
        $plcProj = Join-Path $plcDir 'MyPlc.plcproj'
        Set-Content -LiteralPath $plcProj -Value '<Project />' -Encoding UTF8

        $result = Find-TcPlcProjFile -SolutionPath $script:slnPath -PlcName 'MyPlc'
        $result | Should -Be $plcProj
    }

    It 'throws when no .plcproj matches the PLC name' {
        { Find-TcPlcProjFile -SolutionPath $script:slnPath -PlcName 'Missing' } |
            Should -Throw '*No .plcproj file found*'
    }

    It 'throws when more than one .plcproj matches the PLC name' {
        $a = Join-Path $script:tempRoot 'A\Dup.plcproj'
        $b = Join-Path $script:tempRoot 'B\Dup.plcproj'
        New-Item -ItemType Directory -Path (Split-Path -Parent $a) -Force | Out-Null
        New-Item -ItemType Directory -Path (Split-Path -Parent $b) -Force | Out-Null
        Set-Content -LiteralPath $a -Value '<Project />' -Encoding UTF8
        Set-Content -LiteralPath $b -Value '<Project />' -Encoding UTF8

        { Find-TcPlcProjFile -SolutionPath $script:slnPath -PlcName 'Dup' } |
            Should -Throw '*Multiple .plcproj files*'
    }
}
