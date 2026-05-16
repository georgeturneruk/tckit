#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Repro tests for ConvertTo-HashtableDeep in Start-Bridge.ps1.

.DESCRIPTION
    During the B1 smoke (PR #87) we observed that a JSON-decoded request
    payload with a key literally named "Probes" arrived at the harness
    script as an empty hashtable instead of the caller's string. We
    renamed the parameter to "ReadSymbols" and moved on (see ADR-0010
    section B.3). These tests pin the converter's behaviour for the suspect
    key so the next attempt to land a /symbol-read style route doesn't
    silently reproduce the same bug.

    If these tests pass on the bench machine but the original symptom
    still appears with a parameter literally named Probes, the bug is
    downstream of the converter (in Read-RequestBody, Invoke-Harness's
    splat, or the receiving harness's param binding). Either way, the
    canonical reference for what the converter does lives here.

    The functions under test live in Start-Bridge.ps1 (the bridge
    listener entry point) which can't be dot-sourced cleanly because it
    starts the listener at module-import time. We re-define the
    functions inline here to exercise them in isolation.
#>

BeforeAll {
    function ConvertTo-HashtableDeep {
        param($Object)
        if ($null -eq $Object) { return $null }
        if ($Object -is [System.Collections.IDictionary]) { return $Object }
        if ($Object -is [System.Management.Automation.PSCustomObject]) {
            $ht = @{}
            foreach ($prop in $Object.PSObject.Properties) {
                $ht[$prop.Name] = ConvertTo-HashtableDeep -Object $prop.Value
            }
            return $ht
        }
        if ($Object -is [System.Collections.IEnumerable] -and $Object -isnot [string]) {
            return @($Object | ForEach-Object { ConvertTo-HashtableDeep -Object $_ })
        }
        return $Object
    }
}

Describe 'ConvertTo-HashtableDeep' {
    It 'preserves a string value under a generic key' {
        $json = '{"ReadSymbols": "MAIN.suite.Tests[1].TestIsFailed\nMAIN.suite.Tests[2].TestIsFailed"}'
        $parsed = $json | ConvertFrom-Json
        $ht = ConvertTo-HashtableDeep -Object $parsed

        $ht | Should -BeOfType [System.Collections.IDictionary]
        $ht.ReadSymbols | Should -BeOfType [string]
        $ht.ReadSymbols | Should -Match 'TestIsFailed'
    }

    It 'preserves a string value under the literal key name "Probes" (#84-adjacent)' {
        $json = '{"Probes": "MAIN.suite.Tests[1].TestIsFailed"}'
        $parsed = $json | ConvertFrom-Json
        $ht = ConvertTo-HashtableDeep -Object $parsed

        $ht.Probes | Should -BeOfType [string]
        $ht.Probes | Should -Be 'MAIN.suite.Tests[1].TestIsFailed'
    }

    It 'walks nested objects without flattening string fields' {
        $json = '{"Outer": {"Probes": "value", "Other": 42}}'
        $parsed = $json | ConvertFrom-Json
        $ht = ConvertTo-HashtableDeep -Object $parsed

        $ht.Outer | Should -BeOfType [System.Collections.IDictionary]
        $ht.Outer.Probes | Should -Be 'value'
        $ht.Outer.Other | Should -Be 42
    }

    It 'returns arrays of hashtables for arrays of objects' {
        $json = '{"Items": [{"Name": "a"}, {"Name": "b"}]}'
        $parsed = $json | ConvertFrom-Json
        $ht = ConvertTo-HashtableDeep -Object $parsed

        $ht.Items.Count | Should -Be 2
        $ht.Items[0].Name | Should -Be 'a'
        $ht.Items[1].Name | Should -Be 'b'
    }
}
