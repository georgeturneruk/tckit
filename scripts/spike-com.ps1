<#
.SYNOPSIS
    Phase 2 spike: validate TcXaeShell.DTE.17.0 COM attach on TwinCAT 4026.

.DESCRIPTION
    Attempts to attach to a running TcXaeShell instance via GetActiveObject().
    Prints the DTE object type and version if successful.

    Run this on the Windows machine with XAE open before implementing
    the automation_writer or xae_com_builder adapters.

.EXAMPLE
    .\scripts\spike-com.ps1
#>

$comVersion = $env:COM_VERSION ?? '17.0'
$progId = "TcXaeShell.DTE.$comVersion"

Write-Host "Attempting GetActiveObject('$progId')..."

try {
    $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)
    Write-Host "SUCCESS"
    Write-Host "Type   : $($dte.GetType().FullName)"
    Write-Host "Version: $($dte.Version)"
    Write-Host "Edition: $($dte.Edition)"

    Write-Host ""
    Write-Host "Enumerating solution projects..."
    foreach ($proj in $dte.Solution.Projects) {
        Write-Host "  Project: $($proj.Name) [$($proj.Kind)]"
    }
}
catch [System.Runtime.InteropServices.COMException] {
    Write-Host "FAILED — no active TcXaeShell instance found."
    Write-Host "Make sure TwinCAT XAE is open with a solution loaded."
    Write-Host "Error: $_"
    exit 1
}
catch {
    Write-Host "FAILED — unexpected error:"
    Write-Host $_
    exit 1
}
