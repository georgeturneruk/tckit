#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Tests for Get-TcUnitDefaultXmlPath's fallback ladder and the helper
    surface around it (Get-TcUnitXmlResolveWarning,
    Resolve-TcUnitXmlCandidates). See ADR-0011.

.DESCRIPTION
    Filesystem-only — uses TestDrive plus env-var overrides to exercise
    each branch of the resolution ladder.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcUnit.psm1') -Force
}

Describe 'Get-TcUnitDefaultXmlPath' {
    BeforeEach {
        $script:OriginalProgramData = $env:ProgramData
    }
    AfterEach {
        Remove-Item Env:TCKIT_TCUNIT_XML_PATH -ErrorAction SilentlyContinue
        if ($script:OriginalProgramData) {
            $env:ProgramData = $script:OriginalProgramData
        } else {
            Remove-Item Env:ProgramData -ErrorAction SilentlyContinue
        }
    }

    Context 'env override' {
        It 'returns TCKIT_TCUNIT_XML_PATH when set, without checking filesystem' {
            $env:TCKIT_TCUNIT_XML_PATH = 'D:\custom\path\tcunit_xunit_testresults.xml'
            (Get-TcUnitDefaultXmlPath) | Should -Be 'D:\custom\path\tcunit_xunit_testresults.xml'
            (Get-TcUnitXmlResolveWarning) | Should -Be ''
        }
    }

    Context 'UmRT glob (no kernel-RT file present)' {
        It 'returns the single UmRT candidate when one runtime is installed' {
            $umrtRoot = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_Default\3.1\Boot'
            New-Item -ItemType Directory -Path $umrtRoot -Force | Out-Null
            $umrtPath = Join-Path $umrtRoot 'tcunit_xunit_testresults.xml'
            Set-Content -LiteralPath $umrtPath -Value '<testsuites/>'
            $env:ProgramData = $TestDrive

            $result = Get-TcUnitDefaultXmlPath
            $result | Should -Be $umrtPath
            (Get-TcUnitXmlResolveWarning) | Should -Be ''
        }

        It 'returns the most-recently-modified candidate when multiple runtimes are installed and warns' {
            $rt1 = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_A\3.1\Boot'
            $rt2 = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_B\3.1\Boot'
            New-Item -ItemType Directory -Path $rt1 -Force | Out-Null
            New-Item -ItemType Directory -Path $rt2 -Force | Out-Null
            $path1 = Join-Path $rt1 'tcunit_xunit_testresults.xml'
            $path2 = Join-Path $rt2 'tcunit_xunit_testresults.xml'
            Set-Content -LiteralPath $path1 -Value '<testsuites/>'
            Start-Sleep -Milliseconds 50
            Set-Content -LiteralPath $path2 -Value '<testsuites/>'
            (Get-Item $path2).LastWriteTime = (Get-Item $path1).LastWriteTime.AddSeconds(5)
            $env:ProgramData = $TestDrive

            (Get-TcUnitDefaultXmlPath) | Should -Be $path2
            (Get-TcUnitXmlResolveWarning) | Should -Match 'Multiple UmRT runtimes'
            (Get-TcUnitXmlResolveWarning) | Should -Match 'TCKIT_TCUNIT_XML_PATH'
        }
    }

    Context 'nothing resolves' {
        It 'falls back to the kernel-RT path string even when missing, for stable downstream errors' {
            # ProgramData glob returns nothing; kernel path does not exist on TestDrive.
            $env:ProgramData = $TestDrive
            $result = Get-TcUnitDefaultXmlPath
            $result | Should -Match 'C:\\TwinCAT\\3.1\\Boot\\Plc\\Port_851\\tcunit_xunit_testresults\.xml$'
            (Get-TcUnitXmlResolveWarning) | Should -Be ''
        }
    }

    Context 'warning state is per-call' {
        It 'clears the warning when the next call is unambiguous' {
            $rt1 = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_A\3.1\Boot'
            $rt2 = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_B\3.1\Boot'
            New-Item -ItemType Directory -Path $rt1 -Force | Out-Null
            New-Item -ItemType Directory -Path $rt2 -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $rt1 'tcunit_xunit_testresults.xml') -Value '<testsuites/>'
            Set-Content -LiteralPath (Join-Path $rt2 'tcunit_xunit_testresults.xml') -Value '<testsuites/>'
            $env:ProgramData = $TestDrive

            Get-TcUnitDefaultXmlPath | Out-Null
            (Get-TcUnitXmlResolveWarning) | Should -Match 'Multiple UmRT runtimes'

            $env:TCKIT_TCUNIT_XML_PATH = 'D:\pinned.xml'
            Get-TcUnitDefaultXmlPath | Out-Null
            (Get-TcUnitXmlResolveWarning) | Should -Be ''
        }
    }
}

Describe 'ConvertFrom-TcUnitXml' {
    BeforeAll {
        $script:FixturesDir = Join-Path $PSScriptRoot 'fixtures'
    }

    It 'parses a mixed fixture with FailuresOnly=$false and includes passing tests' {
        $xml = Join-Path $script:FixturesDir 'tcunit-sample-mixed.xml'
        $result = ConvertFrom-TcUnitXml -XmlPath $xml -FailuresOnly $false

        $result.success | Should -BeTrue
        $result.summary.tests | Should -Be 3
        $result.summary.failures | Should -Be 1
        # Both passing and failing suites present, with all testcases inside.
        $allTests = $result.suites | ForEach-Object { $_.tests } | Where-Object { $_ }
        @($allTests).Count | Should -Be 3
        # Flat failures list always carries the failed cases regardless of mode.
        $result.failures.Count | Should -Be 1
        $result.failures[0].suite_name | Should -Be 'FB_Subtracter_Suite'
        $result.failures[0].message | Should -Be 'AssertEquals_INT failed'
    }

    It 'with FailuresOnly=$true strips passing tests and drops empty suites' {
        $xml = Join-Path $script:FixturesDir 'tcunit-sample-mixed.xml'
        $result = ConvertFrom-TcUnitXml -XmlPath $xml -FailuresOnly $true

        $result.success | Should -BeTrue
        # Summary totals always reflect the FULL run, not the narrowed view.
        $result.summary.tests | Should -Be 3
        $result.summary.failures | Should -Be 1
        # Suites narrowed: only suites with at least one failing test, only failing tests inside.
        @($result.suites).Count | Should -Be 1
        $result.suites[0].name | Should -Be 'FB_Subtracter_Suite'
        @($result.suites[0].tests).Count | Should -Be 1
        $result.suites[0].tests[0].passed | Should -BeFalse
        # Flat failures list matches.
        $result.failures.Count | Should -Be 1
    }

    It 'returns success=false for a missing file' {
        $missing = Join-Path $script:FixturesDir 'does-not-exist.xml'
        $result = ConvertFrom-TcUnitXml -XmlPath $missing -FailuresOnly $true
        $result.success | Should -BeFalse
        $result.error | Should -Match 'not found'
    }
}

Describe 'Resolve-TcUnitXmlCandidates' {
    BeforeEach {
        $script:OriginalProgramData = $env:ProgramData
    }
    AfterEach {
        Remove-Item Env:TCKIT_TCUNIT_XML_PATH -ErrorAction SilentlyContinue
        if ($script:OriginalProgramData) {
            $env:ProgramData = $script:OriginalProgramData
        } else {
            Remove-Item Env:ProgramData -ErrorAction SilentlyContinue
        }
    }

    It 'reports env override, kernel path, and UmRT candidates with existence flags' {
        $env:TCKIT_TCUNIT_XML_PATH = 'D:\pinned\does-not-exist.xml'
        $umrtRoot = Join-Path $TestDrive 'Beckhoff\TwinCAT\3.1\Runtimes\UmRT_Default\3.1\Boot'
        New-Item -ItemType Directory -Path $umrtRoot -Force | Out-Null
        $umrtPath = Join-Path $umrtRoot 'tcunit_xunit_testresults.xml'
        Set-Content -LiteralPath $umrtPath -Value '<testsuites/>'
        $env:ProgramData = $TestDrive

        $result = Resolve-TcUnitXmlCandidates

        $result.env_override | Should -Be 'D:\pinned\does-not-exist.xml'
        $result.env_exists | Should -BeFalse
        $result.kernel_path | Should -Match 'C:\\TwinCAT\\3.1\\Boot\\Plc\\Port_851\\'
        $result.umrt_candidates.Count | Should -Be 1
        $result.umrt_candidates[0].path | Should -Be $umrtPath
    }
}
