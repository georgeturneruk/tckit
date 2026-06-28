#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Unit tests for Resolve-TcSolutionConfiguration, which auto-selects a
    solution configuration before deploy so ActivateConfiguration doesn't fail
    with the opaque FindActiveProjectCfgName E_UNEXPECTED (issue #117).

.DESCRIPTION
    The EnvDTE SolutionBuild surface is faked with PSCustomObjects exposing the
    members the helper touches: ActiveConfiguration, SolutionConfigurations
    (a 1-based collection with Count/Item), and each configuration's Name and
    Activate(). No live XAE needed.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcDte.psm1') -Force

    function script:New-FakeConfig {
        param([string]$Name)
        $c = [pscustomobject]@{ Name = $Name; Activated = $false }
        $c | Add-Member -MemberType ScriptMethod -Name Activate -Value { $this.Activated = $true }
        return $c
    }

    function script:New-FakeDte {
        param($Active, [object[]]$Configs = @())
        $collection = [pscustomobject]@{ _c = @($Configs) }
        $collection | Add-Member -MemberType ScriptProperty -Name Count -Value { $this._c.Count }
        $collection | Add-Member -MemberType ScriptMethod -Name Item -Value { param($i) $this._c[$i - 1] }
        $sb = [pscustomobject]@{ ActiveConfiguration = $Active; SolutionConfigurations = $collection }
        $sln = [pscustomobject]@{ SolutionBuild = $sb }
        return [pscustomobject]@{ Solution = $sln }
    }
}

Describe 'Resolve-TcSolutionConfiguration' {
    It 'is a no-op when a configuration is already active' {
        $active = New-FakeConfig 'Release|TwinCAT RT (x64)'
        $others = @((New-FakeConfig 'Debug|TwinCAT OS (x64)'))
        $dte = New-FakeDte -Active $active -Configs $others
        (Resolve-TcSolutionConfiguration -Dte $dte) | Should -Be 'Release|TwinCAT RT (x64)'
        # nothing else activated
        $others[0].Activated | Should -BeFalse
    }

    It 'activates the sole configuration when none is active' {
        $only = New-FakeConfig 'Release|TwinCAT RT (x64)'
        $dte = New-FakeDte -Active $null -Configs @($only)
        (Resolve-TcSolutionConfiguration -Dte $dte) | Should -Be 'Release|TwinCAT RT (x64)'
        $only.Activated | Should -BeTrue
    }

    It 'prefers a Release configuration when several exist and none is active' {
        $debug   = New-FakeConfig 'Debug|TwinCAT OS (x64)'
        $release = New-FakeConfig 'Release|TwinCAT RT (x64)'
        $dte = New-FakeDte -Active $null -Configs @($debug, $release)
        (Resolve-TcSolutionConfiguration -Dte $dte) | Should -Be 'Release|TwinCAT RT (x64)'
        $release.Activated | Should -BeTrue
        $debug.Activated   | Should -BeFalse
    }

    It 'honours an explicit -Prefer over Release' {
        $debug   = New-FakeConfig 'Debug|TwinCAT OS (x64)'
        $release = New-FakeConfig 'Release|TwinCAT RT (x64)'
        $dte = New-FakeDte -Active $null -Configs @($debug, $release)
        (Resolve-TcSolutionConfiguration -Dte $dte -Prefer 'Debug') | Should -Be 'Debug|TwinCAT OS (x64)'
        $debug.Activated   | Should -BeTrue
        $release.Activated | Should -BeFalse
    }

    It 'throws with the candidate list when several exist and none matches Prefer' {
        $a = New-FakeConfig 'Debug|TwinCAT OS (x64)'
        $b = New-FakeConfig 'Debug|TwinCAT OS (ARMV7-A)'
        $dte = New-FakeDte -Active $null -Configs @($a, $b)
        { Resolve-TcSolutionConfiguration -Dte $dte } |
            Should -Throw '*none matches ''Release''*'
    }

    It 'throws a clear error when there are no configurations at all' {
        $dte = New-FakeDte -Active $null -Configs @()
        { Resolve-TcSolutionConfiguration -Dte $dte } |
            Should -Throw '*No solution configuration is available*'
    }
}
