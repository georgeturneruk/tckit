#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Smoke tests for Invoke-TcRuntime.ps1 with TcXaeMgmt's Restart-TwinCAT
    mocked. Runs without a live runtime — verifies the wiring only.

.DESCRIPTION
    The harness script delegates to Restart-TwinCAT. These tests assert
    that the right cmdlet arguments are forwarded for each Mode, and that
    the script's return shape stays consistent.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    $script:ScriptPath = Join-Path $script:HarnessDir 'Invoke-TcRuntime.ps1'
}

Describe 'Invoke-TcRuntime.ps1 (mocked Restart-TwinCAT)' {
    BeforeEach {
        # Stub TcXaeMgmt so the harness's Import-Module succeeds without
        # the real module installed. The fake module exports a no-op
        # Restart-TwinCAT that records its arguments.
        $script:fakeModuleDir = Join-Path ([IO.Path]::GetTempPath()) ("TcXaeMgmtFake-" + [Guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:fakeModuleDir -Force | Out-Null
        $psd1 = @"
@{
    ModuleVersion = '6.0.0'
    GUID = '$([Guid]::NewGuid())'
    RootModule = 'TcXaeMgmt.psm1'
    FunctionsToExport = @('Restart-TwinCAT')
}
"@
        Set-Content -LiteralPath (Join-Path $script:fakeModuleDir 'TcXaeMgmt.psd1') -Value $psd1
        Set-Content -LiteralPath (Join-Path $script:fakeModuleDir 'TcXaeMgmt.psm1') -Value @'
function Restart-TwinCAT {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipelineByPropertyName)]$NetId,
        $Command,
        [switch]$Force,
        [switch]$NoWait,
        [int]$WaitTimeout,
        [switch]$ThrowError
    )
    $global:LastRestartTwinCAT = @{
        NetId       = $NetId
        Command     = $Command
        Force       = [bool]$Force
        NoWait      = [bool]$NoWait
        WaitTimeout = $WaitTimeout
        ThrowError  = [bool]$ThrowError
    }
}
Export-ModuleMember -Function Restart-TwinCAT
'@
        $script:fakeModuleParent = Split-Path -Parent $script:fakeModuleDir
        $script:originalPSModulePath = $env:PSModulePath
        $env:PSModulePath = $script:fakeModuleParent + [IO.Path]::PathSeparator + $env:PSModulePath
        # Pester runs the BeforeAll for the parent module; rename our temp
        # dir to TcXaeMgmt so PSModulePath probing finds it.
        $target = Join-Path $script:fakeModuleParent 'TcXaeMgmt'
        if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Rename-Item -LiteralPath $script:fakeModuleDir -NewName 'TcXaeMgmt'
        $script:fakeModuleDir = $target
        $global:LastRestartTwinCAT = $null
    }

    AfterEach {
        $env:PSModulePath = $script:originalPSModulePath
        Remove-Module TcXaeMgmt -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:fakeModuleDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'forwards Mode=Run as Restart-TwinCAT -Command Restart' {
        $result = & $script:ScriptPath -TargetAmsId '1.2.3.4.1.1' -Mode Run -Wait $false

        $result.success | Should -BeTrue
        $result.details.mode | Should -Be 'Run'
        $result.details.command | Should -Be 'Restart'
        $global:LastRestartTwinCAT.NetId | Should -Be '1.2.3.4.1.1'
        $global:LastRestartTwinCAT.Command | Should -Be 'Restart'
        $global:LastRestartTwinCAT.NoWait | Should -BeTrue
        $global:LastRestartTwinCAT.Force | Should -BeTrue
    }

    It 'forwards Mode=Config as Restart-TwinCAT -Command Config' {
        $result = & $script:ScriptPath -TargetAmsId '1.2.3.4.1.1' -Mode Config -Wait $false

        $result.success | Should -BeTrue
        $result.details.command | Should -Be 'Config'
        $global:LastRestartTwinCAT.Command | Should -Be 'Config'
    }

    It 'passes Wait=$true through as -WaitTimeout and no -NoWait' {
        $result = & $script:ScriptPath -TargetAmsId '1.2.3.4.1.1' -Mode Run -Wait $true -WaitTimeoutSec 60

        $result.success | Should -BeTrue
        $global:LastRestartTwinCAT.NoWait | Should -BeFalse
        $global:LastRestartTwinCAT.WaitTimeout | Should -Be 60000
    }

    It 'returns success=false when TargetAmsId is empty' {
        $result = & $script:ScriptPath -TargetAmsId '' -Mode Run -Wait $false

        $result.success | Should -BeFalse
        $result.error | Should -Match 'TargetAmsId'
    }
}
