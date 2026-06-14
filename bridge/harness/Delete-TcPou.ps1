<#
.SYNOPSIS
    Delete a POU (function block, function, program, or interface) from a
    PLC project, refusing to delete a PROGRAM that is still bound to a task.

.DESCRIPTION
    Walks the POU tree under TIPC^<plc>^<plc> Project^POUs to find the POU
    by name, then calls DeleteChild on the POU's parent. PROGRAMs that are
    referenced by a <PouCall><Name> element in any .TcTTO file under the
    sln directory are refused with the offending task name in the error
    message; XAE would otherwise leave a dangling reference.

    Other POU kinds (FB, FUNCTION, INTERFACE) skip the task scan because
    they can't be task-bound; orphan-instance risk surfaces at build time,
    which is the right place to catch it.

    The DeleteChild primitive is ITcSmTreeItem::DeleteChild(BSTR), single
    arg by display name. See:
      https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242837387.html
    and the PlcArchives.cs sample at
      Beckhoff/TC_AI_DOTNET_Samples for the canonical usage.

.PARAMETER ProjectPath
    Absolute path to the .sln file. When omitted, the operation targets the solution already open in the attached XAE.

.PARAMETER PlcName
    Name of the PLC project. Optional if exactly one is present. Falls back
    to PLC_PROJECT_NAME env var.

.PARAMETER Name
    Name of the POU to delete.
#>
param(
    [string]$ProjectPath = '',
    [string]$PlcName     = $env:PLC_PROJECT_NAME,
    [string]$Name,
    [string]$ComVersion  = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
    [string]$XaeMode     = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '_TcDte.psm1') -Force

function Find-TaskReference {
    param(
        [Parameter(Mandatory)][string]$SolutionPath,
        [Parameter(Mandatory)][string]$PouName
    )
    $slnDir = [System.IO.Path]::GetDirectoryName($SolutionPath)
    if (-not (Test-Path -LiteralPath $slnDir)) { return $null }
    $files = @(Get-ChildItem -Path $slnDir -Filter '*.TcTTO' -Recurse -File -ErrorAction SilentlyContinue)
    foreach ($f in $files) {
        try {
            [xml]$doc = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop
        } catch { continue }
        $taskNodes = $doc.SelectNodes('//Task')
        foreach ($task in $taskNodes) {
            $pouCalls = $task.SelectNodes('PouCall')
            foreach ($pouCall in $pouCalls) {
                $nameNode = $pouCall.SelectSingleNode('Name')
                if ($null -ne $nameNode -and $nameNode.InnerText -eq $PouName) {
                    return @{ Task = [string]$task.GetAttribute('Name'); File = $f.FullName }
                }
            }
        }
    }
    return $null
}

try {
    if (-not $Name)        { return @{ success = $false; error = 'Name required.' } }

    $dte = Get-TcDte -ComVersion $ComVersion -Mode $XaeMode
    Use-TcSolution -Dte $dte -Path $ProjectPath | Out-Null
    if (-not $ProjectPath) { $ProjectPath = $dte.Solution.FullName }
    $plcName = Resolve-TcPlcName -Dte $dte -Explicit $PlcName
    $sm = Get-TcSysManager -Dte $dte -PlcName $plcName

    $pous = Get-TcPousFolder -SysManager $sm -PlcName $plcName
    if ($Name -eq $pous.Name) {
        return @{ success = $false; error = "Refusing to delete the POUs folder itself." }
    }
    $item = Find-TcChild -Root $pous -Name $Name
    if ($null -eq $item) {
        return @{ success = $false; error = "POU '$Name' not found under POUs of '$plcName'." }
    }

    # Validate the found item is actually a POU kind (FB, function, program,
    # interface). A folder of the same name would otherwise be deleted as a
    # POU; the dedicated delete_folder primitive is the right tool for that.
    $pouKinds = @(
        (Get-TcKind -Type 'function_block'),
        (Get-TcKind -Type 'function'),
        (Get-TcKind -Type 'program'),
        (Get-TcKind -Type 'interface')
    )
    # NB: the kind constant (604 for FB, 602 for PROGRAM, 615 for GVL, etc.)
    # lives on ItemType, not ItemSubType. ItemSubType is 0 for source-tree
    # items in this XAE version and is used for I/O sub-discrimination.
    $subType = 0
    try { $subType = [int]$item.ItemType } catch { $subType = 0 }
    if ($pouKinds -notcontains $subType) {
        return @{
            success = $false
            error = "'$Name' is not a POU (kind=$subType). Use the matching delete tool (delete_folder, delete_gvl, delete_dut)."
        }
    }

    $programKind = Get-TcKind -Type 'program'
    if ($subType -eq $programKind) {
        $taskRef = Find-TaskReference -SolutionPath $ProjectPath -PouName $Name
        if ($null -ne $taskRef) {
            return @{
                success = $false
                error = "PROGRAM '$Name' is bound to task '$($taskRef.Task)' in $($taskRef.File). Remove the PouCall first."
            }
        }
    }

    $parentPath = Remove-TcTreeItem -SysManager $sm -Item $item
    Save-TcSolution -Dte $dte

    return @{
        success = $true
        details = @{ name = $Name; plc = $plcName; parent_path = $parentPath; kind = $subType }
    }
}
catch {
    try { Save-TcSolution -Dte $dte } catch { }
    return @{ success = $false; error = $_.Exception.Message }
}
