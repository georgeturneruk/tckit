#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Unit tests for ConvertTo-TcErrorRow in _TcDte.psm1.

.DESCRIPTION
    ConvertTo-TcErrorRow normalises one Error List row (raw column strings)
    into a structured diagnostic row plus its severity bucket. It is the pure,
    testable core shared by the Error List readers; the UI Automation traversal
    that feeds it (Read-TcErrorListUia) needs a live XAE GUI and is verified
    manually, not here. See ADR-0014.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcDte.psm1') -Force
}

Describe 'ConvertTo-TcErrorRow' {
    Context 'severity-label to bucket mapping' {
        It 'maps "Error" to the errors bucket' {
            $r = ConvertTo-TcErrorRow -Severity 'Error' -Description 'boom'
            $r.bucket | Should -Be 'errors'
            $r.row.severity | Should -Be 'error'
        }

        It 'maps "Warning" to the warnings bucket' {
            $r = ConvertTo-TcErrorRow -Severity 'Warning' -Description 'careful'
            $r.bucket | Should -Be 'warnings'
            $r.row.severity | Should -Be 'warning'
        }

        It 'maps "Message" to the infos bucket' {
            $r = ConvertTo-TcErrorRow -Severity 'Message' -Description 'just so you know'
            $r.bucket | Should -Be 'infos'
            $r.row.severity | Should -Be 'info'
        }

        It 'maps an empty severity to the infos bucket (unrealised / merged row)' {
            $r = ConvertTo-TcErrorRow -Severity '' -Description 'no severity column text'
            $r.bucket | Should -Be 'infos'
            $r.row.severity | Should -Be 'info'
        }

        It 'is case-insensitive and tolerant of trailing text' {
            (ConvertTo-TcErrorRow -Severity 'error').bucket   | Should -Be 'errors'
            (ConvertTo-TcErrorRow -Severity 'WARNING ').bucket | Should -Be 'warnings'
            (ConvertTo-TcErrorRow -Severity 'Information').bucket | Should -Be 'infos'
        }

        It 'treats an unrecognised non-empty severity as an error (never silently downgraded)' {
            $r = ConvertTo-TcErrorRow -Severity 'Critical' -Description 'unknown label'
            $r.bucket | Should -Be 'errors'
        }
    }

    Context 'compiler-code extraction' {
        It 'lifts a leading "C0046: ..." code out of the description when no Code is given' {
            $r = ConvertTo-TcErrorRow -Severity 'Error' -Description "C0046: Identifier 'x' not defined"
            $r.row.code    | Should -Be 'C0046'
            $r.row.message | Should -Be "Identifier 'x' not defined"
        }

        It 'prefers an explicit Code column and leaves the description untouched' {
            $r = ConvertTo-TcErrorRow -Severity 'Error' -Code 'C0018' -Description "'x' is no valid assignment target"
            $r.row.code    | Should -Be 'C0018'
            $r.row.message | Should -Be "'x' is no valid assignment target"
        }

        It 'leaves code empty and message intact for a TwinCAT message with no code' {
            $r = ConvertTo-TcErrorRow -Severity 'Message' -Description "'PlcTask' (350): | Tests: 28"
            $r.row.code    | Should -Be ''
            $r.row.message | Should -Be "'PlcTask' (350): | Tests: 28"
        }
    }

    Context 'field passthrough and coercion' {
        It 'passes file / line / project through and coerces line to int' {
            $r = ConvertTo-TcErrorRow -Severity 'Error' -Code 'C0046' `
                    -Description 'msg' -File 'MAIN.TcPOU (Impl)' -Line 12 -Project 'T3TckitUtils_Plc'
            $r.row.file    | Should -Be 'MAIN.TcPOU (Impl)'
            $r.row.line    | Should -Be 12
            $r.row.line    | Should -BeOfType [int]
            $r.row.project | Should -Be 'T3TckitUtils_Plc'
        }

        It 'returns the six expected row keys' {
            $r = ConvertTo-TcErrorRow -Severity 'Error' -Description 'msg'
            ($r.row.Keys | Sort-Object) | Should -Be @('code', 'file', 'line', 'message', 'project', 'severity')
        }
    }
}
