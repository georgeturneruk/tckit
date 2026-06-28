#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Unit tests for Resolve-TcFolderPath's folder-path traversal, in particular
    the leading-segment tolerance that lets the reader's folder value
    ("POUs/Drives", "DUTs/RingBuffer") be passed verbatim as parent_folder.

.DESCRIPTION
    The reader (_folder_for in xml_reader.py) reports a POU/DUT/GVL folder
    WITH its type-root segment, but Resolve-TcFolderPath starts AT that root
    node. A single leading segment that names the root is dropped so both
    "Drives" and "POUs/Drives" resolve to the same node (reader/writer
    symmetry). These tests pin that behaviour without needing a live XAE.

    Tree items are faked with PSCustomObjects exposing the small surface the
    function touches: Name, ChildCount, Child($i) (1-based) and PathName.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcDte.psm1') -Force

    function script:New-FakeTreeItem {
        param(
            [string]$Name,
            [object[]]$Children = @(),
            [string]$PathName = ''
        )
        $item = [pscustomobject]@{
            Name      = $Name
            _children = @($Children)
            PathName  = if ($PathName) { $PathName } else { $Name }
        }
        $item | Add-Member -MemberType ScriptProperty -Name ChildCount -Value { $this._children.Count }
        $item | Add-Member -MemberType ScriptMethod -Name Child -Value { param($i) $this._children[$i - 1] }
        return $item
    }

    # POUs root with a "RingBuffer" subfolder that itself holds a "Deep" folder.
    function script:New-PousTree {
        $deep = New-FakeTreeItem -Name 'Deep'
        $ring = New-FakeTreeItem -Name 'RingBuffer' -Children @($deep)
        $strings = New-FakeTreeItem -Name 'Strings'
        return New-FakeTreeItem -Name 'POUs' -Children @($ring, $strings)
    }
}

Describe 'Resolve-TcFolderPath' {
    It 'returns the root unchanged for an empty path' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path '').Name | Should -Be 'POUs'
    }

    It 'resolves a plain subfolder name relative to the root' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path 'RingBuffer').Name | Should -Be 'RingBuffer'
    }

    It 'drops a leading segment that names the root (POUs/RingBuffer)' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path 'POUs/RingBuffer').Name | Should -Be 'RingBuffer'
    }

    It 'drops the leading root segment for a DUTs root too' {
        $sub = New-FakeTreeItem -Name 'RingBuffer'
        $root = New-FakeTreeItem -Name 'DUTs' -Children @($sub)
        (Resolve-TcFolderPath -Root $root -Path 'DUTs/RingBuffer').Name | Should -Be 'RingBuffer'
    }

    It 'resolves a nested path' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path 'RingBuffer/Deep').Name | Should -Be 'Deep'
    }

    It 'resolves a nested path with the leading root segment present' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path 'POUs/RingBuffer/Deep').Name | Should -Be 'Deep'
    }

    It 'accepts backslash separators' {
        $root = New-PousTree
        (Resolve-TcFolderPath -Root $root -Path 'POUs\RingBuffer').Name | Should -Be 'RingBuffer'
    }

    It 'only strips ONE leading root segment' {
        # A real subfolder literally named "POUs" under the POUs root: after
        # one strip, "POUs/POUs" must still resolve to that nested folder.
        $nested = New-FakeTreeItem -Name 'POUs'
        $root = New-FakeTreeItem -Name 'POUs' -Children @($nested)
        $resolved = Resolve-TcFolderPath -Root $root -Path 'POUs/POUs'
        $resolved.Name | Should -Be 'POUs'
        $resolved.ChildCount | Should -Be 0   # the nested one, not the root
    }

    It 'throws a precise error when a segment is missing' {
        $root = New-PousTree
        { Resolve-TcFolderPath -Root $root -Path 'RingBuffer/Nope' } |
            Should -Throw "*'Nope' not found*"
    }
}
