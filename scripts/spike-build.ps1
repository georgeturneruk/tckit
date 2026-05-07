<#
.SYNOPSIS
    Phase 2 spike: validate build trigger + ErrorList shape on TwinCAT 4026.

.DESCRIPTION
    Triggers a synchronous build of the loaded solution and walks the
    DTE.ToolWindows.ErrorList.ErrorItems collection to capture the property
    names and severity values used for build errors. Confirms the contract
    that Invoke-TcBuild.ps1 will rely on.

    SAFE TO RUN — read-only after the build itself.

.PARAMETER ComVersion
    DTE COM version. Default 17.0.

.EXAMPLE
    .\scripts\spike-build.ps1
#>
param(
    [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' })
)

$ErrorActionPreference = 'Stop'

$progId = "TcXaeShell.DTE.$ComVersion"
Write-Host "Attaching to $progId..."
$dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)

if ($dte.Solution.Projects.Count -eq 0) { throw 'No projects loaded.' }

Write-Host "Triggering build (synchronous)..."
$start = Get-Date
$dte.Solution.SolutionBuild.Build($true)   # $true = wait for completion
$elapsed = (Get-Date) - $start
Write-Host ("Build completed in {0:N2}s" -f $elapsed.TotalSeconds)
Write-Host "LastBuildInfo (number of failed projects): $($dte.Solution.SolutionBuild.LastBuildInfo)"

# Walk the error list.
Write-Host ''
Write-Host '=== ErrorList walk ==='
try {
    $errorList = $dte.ToolWindows.ErrorList
    Write-Host "ErrorList type: $($errorList.GetType().FullName)"

    $items = $errorList.ErrorItems
    Write-Host "ErrorItems count: $($items.Count)"

    if ($items.Count -gt 0) {
        Write-Host ''
        Write-Host "First item property dump (call .GetType().GetProperties()):"
        $first = $items.Item(1)
        foreach ($p in $first.GetType().GetProperties()) {
            try {
                $val = $p.GetValue($first, $null)
                $valStr = if ($null -eq $val) { '<null>' } else { $val.ToString() }
                if ($valStr.Length -gt 120) { $valStr = $valStr.Substring(0, 120) + '...' }
                Write-Host ("  {0,-20} = {1}" -f $p.Name, $valStr)
            } catch { Write-Host "  $($p.Name) = <unreadable>" }
        }

        Write-Host ''
        Write-Host 'All items (file:line | level | description):'
        for ($i = 1; $i -le $items.Count; $i++) {
            $it = $items.Item($i)
            $file  = try { $it.FileName } catch { '?' }
            $line  = try { $it.Line } catch { 0 }
            $level = try { $it.ErrorLevel } catch { '?' }
            $desc  = try { $it.Description } catch { '?' }
            Write-Host ("  {0}:{1} | level={2} | {3}" -f $file, $line, $level, $desc)
        }
    } else {
        Write-Host '(build had no errors / warnings — try again with a deliberately broken POU to inspect the error shape)' -ForegroundColor Yellow
    }
} catch {
    Write-Host "ErrorList access FAILED: $_" -ForegroundColor Red
    throw
}

Write-Host ''
Write-Host 'Spike complete. Confirm in SPIKE_NOTES.md:'
Write-Host '  - Build($true) is synchronous (returns when done)'
Write-Host '  - DTE.ToolWindows.ErrorList.ErrorItems exposes (FileName, Line, Description, ErrorLevel)'
Write-Host '  - ErrorLevel values: low=message, medium=warning, high=error (vsBuildErrorLevel enum)'
