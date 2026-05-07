<#
.SYNOPSIS
    Shared DTE / TwinCAT automation-interface helpers used by harness scripts.

.DESCRIPTION
    Centralises the COM-attach + tree-navigation + source-write logic so
    individual harness scripts don't reimplement it. Exported functions:

      Get-TcDte           — attach to (or spawn) TcXaeShell.DTE.<ver>
      Open-TcSolution     — open a .sln file if not already loaded
      Resolve-TcPlcName   — pick the PLC project name (explicit or auto)
      Get-TcPousFolder    — return the POUs folder under a PLC project
      Find-TcChild        — depth-first find of a child by name under a node
      Get-TcKind          — map logical type names to CreateChild kind constants
      Set-TcItemSource    — write declaration + implementation to a tree item
      Split-TcCode        — split combined ST source into declaration + body
      Invoke-TcDevenvBuild — shell out to devenv.exe /rebuild /log Log.xml
      Read-TcBuildLog     — parse a devenv build-log XML into structured errors

    Tree paths follow the doubled-name pattern documented at
    https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html :
        TIPC^<plc>^<plc> Project^POUs^<POU>
    The " Project" suffix on the second occurrence is required.
#>

Set-StrictMode -Version Latest

# ------------------------------------------------------------------
# Tree-item kind constants (from Beckhoff infosys, validated by spike)
# ------------------------------------------------------------------

$script:TcKind = @{
    Folder            = 601
    Program           = 602
    Function          = 603
    FunctionBlock     = 604
    Enum              = 605
    Struct            = 606
    Union             = 607
    Action            = 608
    Method            = 609
    InterfaceMethod   = 610
    Property          = 611
    InterfaceProperty = 612
    PropertyGet       = 613
    PropertySet       = 614
    GVL               = 615
    Transition        = 616
    Interface         = 618
    PlcProject        = 0  # special: passed with template name in 4th arg
}

function Get-TcKind {
    <#
    .SYNOPSIS
        Map a logical type name to its CreateChild kind constant.

        Accepts: function_block, function, program, interface, method, action,
        property, gvl, folder.
    #>
    param([Parameter(Mandatory)][string]$Type)
    switch ($Type.ToLowerInvariant()) {
        'function_block' { return $script:TcKind.FunctionBlock }
        'function'       { return $script:TcKind.Function }
        'program'        { return $script:TcKind.Program }
        'interface'      { return $script:TcKind.Interface }
        'method'         { return $script:TcKind.Method }
        'action'         { return $script:TcKind.Action }
        'property'       { return $script:TcKind.Property }
        'gvl'            { return $script:TcKind.GVL }
        'folder'         { return $script:TcKind.Folder }
        default {
            throw "Unknown tree-item type: '$Type'."
        }
    }
}

# ------------------------------------------------------------------
# DTE attach
# ------------------------------------------------------------------

function Get-TcDte {
    param(
        [string]$ComVersion = $(if ($env:COM_VERSION) { $env:COM_VERSION } else { '17.0' }),
        [ValidateSet('attach', 'headless')][string]$Mode = $(if ($env:XAE_MODE) { $env:XAE_MODE } else { 'attach' })
    )

    $progId = "TcXaeShell.DTE.$ComVersion"
    try {
        return [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)
    } catch [System.Runtime.InteropServices.COMException] {
        if ($Mode -ne 'headless') {
            throw "No active $progId instance found, and Mode is 'attach'. Open TwinCAT XAE or set XAE_MODE=headless."
        }
    }

    try {
        $type = [Type]::GetTypeFromProgID($progId)
        if ($null -eq $type) { throw "ProgID '$progId' not registered on this machine." }
        $dte = [System.Activator]::CreateInstance($type)
        try { $dte.SuppressUI = $true } catch { }
        return $dte
    } catch {
        throw "Failed to spawn $progId in headless mode: $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------
# Solution / project navigation
# ------------------------------------------------------------------

function Open-TcSolution {
    param(
        [Parameter(Mandatory)]$Dte,
        [Parameter(Mandatory)][string]$Path
    )
    if (-not (Test-Path $Path)) { throw "Solution path not found: $Path" }
    $resolved = (Resolve-Path $Path).Path
    $current = ''
    try { $current = $Dte.Solution.FullName } catch { }
    if ($current -ne $resolved) { $Dte.Solution.Open($resolved) }
    return $Dte.Solution
}

function Get-TcSysManager {
    <#
    .SYNOPSIS
        Find the loaded TwinCAT project's ITcSysManager. Probes by trying
        LookupTreeItem('TIPC') because all COM objects share GetType().Name.

    .NOTES
        ITcSmTreeItem exposes _NewEnum (its child enumerator), which means
        PowerShell treats the object itself as a collection and unrolls it
        when emitted via `return`. We use Write-Output -NoEnumerate to
        prevent unrolling — same trick is used by all helpers below that
        return a tree item.
    #>
    param([Parameter(Mandatory)]$Dte)

    if ($Dte.Solution.Projects.Count -eq 0) {
        throw 'No projects in active solution. Call Open-TcSolution first.'
    }
    foreach ($proj in $Dte.Solution.Projects) {
        $obj = $null
        try { $obj = $proj.Object } catch { continue }
        if ($null -eq $obj) { continue }
        try {
            $obj.LookupTreeItem('TIPC') | Out-Null
            Write-Output $obj -NoEnumerate
            return
        } catch { continue }
    }
    throw 'No TwinCAT project (ITcSysManager) found in solution.'
}

function Resolve-TcPlcName {
    <#
    .SYNOPSIS
        Pick the PLC project name to operate on. If $Explicit is non-empty, use
        it. Otherwise: if exactly one PLC project exists under TIPC, use that.
        If multiple exist and no name was provided, throw.
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [string]$Explicit = ''
    )
    if ($Explicit) { return $Explicit }
    $tipc = $SysManager.LookupTreeItem('TIPC')
    if ($tipc.ChildCount -eq 0) {
        throw 'No PLC projects under TIPC. Add one (or pass -PlcName explicitly).'
    }
    if ($tipc.ChildCount -gt 1) {
        $names = @()
        for ($i = 1; $i -le $tipc.ChildCount; $i++) { $names += $tipc.Child($i).Name }
        throw "Multiple PLC projects under TIPC ($($names -join ', ')). Pass -PlcName to disambiguate."
    }
    return $tipc.Child(1).Name
}

function Get-TcPlcProjectNode {
    <#
    .SYNOPSIS
        Return the PLC project tree item at TIPC^<plc>^<plc> Project. This
        node is where source items (POUs, GVLs, DUTs) live underneath.
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName^$PlcName Project")
    Write-Output $node -NoEnumerate
}

function Get-TcPousFolder {
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName^$PlcName Project^POUs")
    Write-Output $node -NoEnumerate
}

function Find-TcChild {
    <#
    .SYNOPSIS
        Depth-first find by name under a tree-item root.
    #>
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$Name
    )
    if ($Root.Name -eq $Name) { Write-Output $Root -NoEnumerate; return }
    if ($Root.ChildCount -lt 1) { return $null }
    for ($i = 1; $i -le $Root.ChildCount; $i++) {
        $found = Find-TcChild -Root $Root.Child($i) -Name $Name
        if ($null -ne $found) { Write-Output $found -NoEnumerate; return }
    }
    return $null
}

# ------------------------------------------------------------------
# Source code write
# ------------------------------------------------------------------

function Split-TcCode {
    <#
    .SYNOPSIS
        Split combined ST source into a declaration block and an implementation
        body. Splits at the last line that consists solely of "END_VAR" (with
        optional whitespace). If no END_VAR is found, returns ($Code, '').

    .OUTPUTS
        @{ declaration = string; implementation = string }
    #>
    param([Parameter(Mandatory)][string]$Code)

    $code = $Code -replace "`r`n", "`n"
    $matches = [regex]::Matches($code, '(?m)^[ \t]*END_VAR[ \t]*$')
    if ($matches.Count -eq 0) {
        return @{ declaration = $Code; implementation = '' }
    }
    $last = $matches[$matches.Count - 1]
    $cut = $last.Index + $last.Length
    $decl = $code.Substring(0, $cut)
    $impl = if ($cut -lt $code.Length) { $code.Substring($cut).TrimStart("`n", "`r", " ", "`t") } else { '' }
    return @{ declaration = $decl; implementation = $impl }
}

function Set-TcItemSource {
    <#
    .SYNOPSIS
        Write declaration and implementation to a tree item via the
        ITcPlcDeclaration.DeclarationText and ITcPlcImplementation.
        ImplementationText properties. PowerShell's COM dispatch finds these
        without an explicit interface cast.

    .DESCRIPTION
        Accepts either pre-split inputs (-Declaration + -Implementation) or
        a combined -Code string (split via Split-TcCode).
    #>
    param(
        [Parameter(Mandatory)]$Item,
        [string]$Declaration = $null,
        [string]$Implementation = $null,
        [string]$Code = $null
    )
    if ($Code) {
        $parts = Split-TcCode -Code $Code
        $Declaration = $parts.declaration
        $Implementation = $parts.implementation
    }
    if ($null -ne $Declaration) { $Item.DeclarationText = $Declaration }
    if ($null -ne $Implementation) { $Item.ImplementationText = $Implementation }
}

# ------------------------------------------------------------------
# Build via devenv.exe (Express edition has no ToolWindows.ErrorList)
# ------------------------------------------------------------------

function Find-Devenv {
    <#
    .SYNOPSIS
        Resolve the path to TcXaeShell.exe / devenv.exe to invoke for /rebuild
        /log. Honours $env:DEVENV_PATH if set.
    #>
    if ($env:DEVENV_PATH -and (Test-Path $env:DEVENV_PATH)) { return $env:DEVENV_PATH }
    $candidates = @(
        'C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe',
        'C:\Program Files (x86)\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "Could not locate TcXaeShell.exe. Set DEVENV_PATH env var."
}

function Invoke-TcDevenvBuild {
    <#
    .SYNOPSIS
        Run TcXaeShell.exe /rebuild "<config>|<platform>" /log <logPath> <sln>.
        Returns the exit code; the structured log lands at $LogPath.
    #>
    param(
        [Parameter(Mandatory)][string]$SolutionPath,
        [Parameter(Mandatory)][string]$LogPath,
        [string]$Configuration = 'Release',
        [string]$Platform = 'TwinCAT RT (x64)'
    )
    $devenv = Find-Devenv
    if (Test-Path $LogPath) { Remove-Item $LogPath -Force }
    $args = @(
        $SolutionPath,
        '/rebuild', "$Configuration|$Platform",
        '/log', $LogPath
    )
    $proc = Start-Process -FilePath $devenv -ArgumentList $args -Wait -PassThru -NoNewWindow
    return $proc.ExitCode
}

function Read-TcBuildLog {
    <#
    .SYNOPSIS
        Parse a TcXaeShell /log XML file into structured errors and warnings.

    .OUTPUTS
        @{ errors   = @( @{file; line; message; severity='error'},   ... )
           warnings = @( @{file; line; message; severity='warning'}, ... ) }
    #>
    param([Parameter(Mandatory)][string]$LogPath)

    $errors = @()
    $warnings = @()
    if (-not (Test-Path $LogPath)) {
        return @{ errors = $errors; warnings = $warnings }
    }
    [xml]$doc = Get-Content -Path $LogPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $doc) { return @{ errors = $errors; warnings = $warnings } }

    # The /log XML is <activity><entry type="error|warning" .../></activity>.
    # Some entries have a path-like description; we use a regex to pull out
    # file(line,col): ERR_OR_WRN: text style strings if present.
    $entries = $doc.SelectNodes('//entry')
    foreach ($e in $entries) {
        $type = ([string]$e.type).ToLowerInvariant()
        if ($type -ne 'error' -and $type -ne 'warning') { continue }

        $text = [string]$e.InnerText
        $file = ''; $line = 0; $message = $text
        $m = [regex]::Match($text, '^(?<file>[^()]+)\((?<line>\d+)(?:,\d+)?\)\s*:\s*(?:error|warning)[^:]*:\s*(?<msg>.*)$')
        if ($m.Success) {
            $file    = $m.Groups['file'].Value.Trim()
            $line    = [int]$m.Groups['line'].Value
            $message = $m.Groups['msg'].Value.Trim()
        }
        $row = @{ file = $file; line = $line; message = $message; severity = $type }
        if ($type -eq 'error') { $errors += $row } else { $warnings += $row }
    }
    return @{ errors = $errors; warnings = $warnings }
}

# ------------------------------------------------------------------
# Module exports
# ------------------------------------------------------------------

Export-ModuleMember -Function `
    Get-TcKind, Get-TcDte, Open-TcSolution, Get-TcSysManager, `
    Resolve-TcPlcName, Get-TcPlcProjectNode, Get-TcPousFolder, Find-TcChild, `
    Set-TcItemSource, Split-TcCode, Find-Devenv, Invoke-TcDevenvBuild, Read-TcBuildLog
