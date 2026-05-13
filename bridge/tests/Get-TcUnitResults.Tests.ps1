#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Fixture-based tests for the TcUnit XML parser in Get-TcUnitResults.ps1.

.DESCRIPTION
    Verifies the parser maps JUnit-style XML produced by TcUnit onto the
    structured TestResults hashtable shape that the Python adapter
    consumes. Hardware-free — runs against captured fixtures only.
#>

BeforeAll {
    $script:HarnessDir  = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    $script:FixturesDir = Join-Path $PSScriptRoot 'fixtures'
    $script:ScriptPath  = Join-Path $script:HarnessDir 'Get-TcUnitResults.ps1'
}

Describe 'Get-TcUnitResults.ps1 (fixture)' {
    It 'parses a passing-only fixture into a green TestResults shape' {
        $xml = Join-Path $script:FixturesDir 'tcunit-sample-passing.xml'
        $result = & $script:ScriptPath -XmlPath $xml

        $result.success | Should -BeTrue
        $result.summary.suites | Should -Be 1
        $result.summary.tests | Should -Be 2
        $result.summary.failures | Should -Be 0
        $result.suites.Count | Should -Be 1
        $result.suites[0].name | Should -Be 'FB_Adder_Suite'
        $result.suites[0].tests.Count | Should -Be 2
        $result.suites[0].tests | ForEach-Object { $_.passed | Should -BeTrue }
    }

    It 'parses a mixed pass/fail fixture and extracts AssertFailure detail' {
        $xml = Join-Path $script:FixturesDir 'tcunit-sample-mixed.xml'
        $result = & $script:ScriptPath -XmlPath $xml

        $result.success | Should -BeTrue
        $result.summary.suites | Should -Be 2
        $result.summary.tests | Should -Be 3
        $result.summary.failures | Should -Be 1

        $failingSuite = $result.suites | Where-Object { $_.name -eq 'FB_Subtracter_Suite' }
        $failingSuite | Should -Not -BeNullOrEmpty
        $failingSuite.tests[0].passed | Should -BeFalse
        $failingSuite.tests[0].failures.Count | Should -Be 1

        $fail = $failingSuite.tests[0].failures[0]
        $fail.message | Should -Be 'AssertEquals_INT failed'
        $fail.expected | Should -Be '1'
        $fail.actual | Should -Be '2'
        $fail.line | Should -Be 42
    }

    It 'returns an error response when the XML file does not exist' {
        $missing = Join-Path $script:FixturesDir 'does-not-exist.xml'
        $result = & $script:ScriptPath -XmlPath $missing

        $result.success | Should -BeFalse
        $result.error | Should -Match 'not found'
    }
}
