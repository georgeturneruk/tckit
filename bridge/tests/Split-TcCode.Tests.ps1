#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Unit tests for Split-TcCode in _TcDte.psm1.

.DESCRIPTION
    Split-TcCode partitions combined ST source into a declaration block and
    an implementation body. The interesting cases are the ones without an
    explicit END_VAR (issue #84), where the splitter has to fall back to
    detecting the POU/method header line.
#>

BeforeAll {
    $script:HarnessDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'harness'
    Import-Module (Join-Path $script:HarnessDir '_TcDte.psm1') -Force
}

Describe 'Split-TcCode' {
    Context 'with explicit END_VAR' {
        It 'splits at the last END_VAR for a typical method body' {
            $code = @"
METHOD Step : INT
VAR_INPUT
    sample : INT;
END_VAR
VAR
    sum : DINT;
END_VAR
sum := DINT#0;
Step := sample;
"@
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Match 'METHOD Step : INT'
            $parts.declaration | Should -Match 'sum : DINT;\s*END_VAR$'
            $parts.implementation | Should -Match '^sum := DINT#0;'
            $parts.implementation | Should -Match 'Step := sample;'
        }

        It 'returns empty implementation when source ends at END_VAR' {
            $code = @"
FUNCTION_BLOCK FB_Foo
VAR
    x : INT;
END_VAR
"@
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Match 'END_VAR'
            $parts.implementation | Should -Be ''
        }
    }

    Context 'without END_VAR (issue #84 fallback)' {
        It 'puts the METHOD header in declaration and the body in implementation' {
            $code = @"
METHOD Step : INT
CASE state OF
    0: state := 1;
END_CASE;
Step := state;
"@
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Be 'METHOD Step : INT'
            $parts.implementation | Should -Match '^CASE state OF'
            $parts.implementation | Should -Match 'Step := state;'
        }

        It 'handles a header-only method whose body is just a no-op' {
            $code = @"
METHOD Step : BOOL
;
"@
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Be 'METHOD Step : BOOL'
            $parts.implementation | Should -Be ';'
        }

        It 'handles a FUNCTION_BLOCK header with a body and no VAR block' {
            $code = @"
FUNCTION_BLOCK FB_Tiny
bDone := TRUE;
"@
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Be 'FUNCTION_BLOCK FB_Tiny'
            $parts.implementation | Should -Be 'bDone := TRUE;'
        }
    }

    Context 'without END_VAR or a header line' {
        It 'treats the whole source as implementation when neither anchor is present' {
            $code = "x := 42;"
            $parts = Split-TcCode -Code $code

            $parts.declaration | Should -Be ''
            $parts.implementation | Should -Be 'x := 42;'
        }
    }
}
