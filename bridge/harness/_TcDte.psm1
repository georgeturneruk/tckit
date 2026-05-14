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
# COM concurrency helpers
# ------------------------------------------------------------------

function Invoke-WithComRetry {
    <#
    .SYNOPSIS
        Retry a COM call that fails with RPC_E_CALL_REJECTED (0x80010001)
        or RPC_E_SERVERCALL_RETRYLATER (0x8001010A).

    .DESCRIPTION
        XAE's single-threaded apartment rejects re-entrant calls when it's
        busy (build in progress, dialog open, UI thread blocked, etc.).
        Microsoft's documented solution is to register an IMessageFilter
        that retries; from PowerShell we can do the same thing with an
        exponential-backoff retry around each COM call. Failures with any
        other HRESULT propagate immediately. See
        https://learn.microsoft.com/previous-versions/office/troubleshoot/office-developer/automation-operation-not-bypass-server
    #>
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [int]$MaxAttempts = 6,
        [int]$BaseDelayMs = 200
    )
    $rpcCallRejected = -2147418111   # 0x80010001
    $rpcRetryLater   = -2147417846   # 0x8001010A
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            return & $ScriptBlock
        } catch [System.Runtime.InteropServices.COMException] {
            $hr = $_.Exception.HResult
            if ($hr -ne $rpcCallRejected -and $hr -ne $rpcRetryLater) { throw }
            if ($attempt -eq $MaxAttempts) { throw }
            Start-Sleep -Milliseconds ($BaseDelayMs * [Math]::Pow(2, $attempt - 1))
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

function Remove-TcStaleLockFile {
    <#
    .SYNOPSIS
        Delete a stale TcXaeShell .~u lock file next to a .sln, if its
        recorded PID no longer exists.

    .DESCRIPTION
        TcXaeShell writes <SolutionName>.~u beside the .sln while a sln
        is loaded, holding owner / hostname / PID / timestamp. The file
        is supposed to be deleted on clean close, but a crashed XAE
        leaves it behind. A subsequent Solution.Open against the same
        sln then hangs or crashes the new XAE instance (reproduced
        on B1 with PID 34460 long after that XAE died).

        This helper reads the PID on line 3 and only deletes the lock
        if no live process has that PID. Best-effort: silently swallows
        all errors so it never blocks an Open.
    #>
    param([Parameter(Mandatory)][string]$SolutionPath)
    try {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($SolutionPath)
        $dir  = [System.IO.Path]::GetDirectoryName($SolutionPath)
        $lock = Join-Path $dir "$stem.~u"
        if (-not (Test-Path -LiteralPath $lock)) { return }
        $linesRaw = Get-Content -LiteralPath $lock -ErrorAction Stop
        $lines = @($linesRaw)
        $pidLine = if ($lines.Count -ge 3) { $lines[2] } else { '' }
        $deadPid = 0
        $isNumeric = [int]::TryParse($pidLine.Trim(), [ref]$deadPid)
        if ($isNumeric -and $deadPid -gt 0) {
            $proc = Get-Process -Id $deadPid -ErrorAction SilentlyContinue
            if ($null -ne $proc) { return }  # genuinely held — leave it
        }
        Remove-Item -LiteralPath $lock -Force -ErrorAction Stop
    } catch { }
}

function Open-TcSolution {
    param(
        [Parameter(Mandatory)]$Dte,
        [Parameter(Mandatory)][string]$Path
    )
    if (-not (Test-Path $Path)) { throw "Solution path not found: $Path" }
    if ($null -eq $Dte.Solution) {
        # TcXaeShell can land in a state where DTE attaches but its
        # Solution property is null — observed after long-idle / repeated
        # build cycles. The fix is operator-side (restart XAE); raise a
        # clear error instead of the cryptic 'cannot call method on null'.
        throw 'TcXaeShell DTE has no Solution object (attached but uninitialised). Restart TcXaeShell and retry.'
    }
    $resolved = (Resolve-Path $Path).Path
    $current = ''
    try { $current = $Dte.Solution.FullName } catch { }
    if ($current -ne $resolved) {
        Remove-TcStaleLockFile -SolutionPath $resolved
        Invoke-WithComRetry { $Dte.Solution.Open($resolved) } | Out-Null
        # After opening an existing sln, PLC source trees can be
        # lazy-loaded — the '<plc> Project' nodes don't appear under TIPC
        # until something touches them, which makes downstream
        # LookupTreeItem('TIPC^<plc>^<plc> Project^...') fail with
        # 'Item not found'. Force-materialise them now.
        try { Wait-TcPlcProjectsLoaded -Dte $Dte | Out-Null } catch { }
    }
    return $Dte.Solution
}

function Save-TcSolution {
    <#
    .SYNOPSIS
        Flush the active TcXaeShell solution to disk via File.SaveAll.

    .DESCRIPTION
        Bridge writes via COM mutate XAE's in-memory model and usually
        get persisted on close. Without an explicit SaveAll, XAE also
        writes its own copy asynchronously, which can race the
        external-file-watcher and produce 'project file has been
        modified outside of TcXaeShell' prompts — and, intermittently,
        crash XAE on the next solution operation. Calling SaveAll after
        each write step keeps the in-memory and on-disk views
        synchronised.

        Best-effort: silently ignores failures (e.g. ExecuteCommand
        rejected during a build) so callers don't have to wrap.
    #>
    param([Parameter(Mandatory)]$Dte)
    try {
        Invoke-WithComRetry { $Dte.ExecuteCommand('File.SaveAll') } | Out-Null
    } catch { }
}

function Wait-TcPlcProjectsLoaded {
    <#
    .SYNOPSIS
        Force-materialise each PLC project's source tree under TIPC.

    .DESCRIPTION
        When TcXaeShell opens an existing .sln from disk, the .plcproj
        source trees underneath each TIPC^<plc> node are lazy-loaded;
        only the project instance (<plc> Instance) is exposed until
        something specifically requests the source. This helper walks
        each PLC project and polls LookupTreeItem("TIPC^<plc>^<plc>
        Project") until it resolves, which is what causes XAE to load
        the source. Without this, the first downstream automation
        interface call against the source tree fails with 'Item not
        found'.

        Best-effort: returns silently if no Solution is loaded or no
        ITcSysManager is found. Caller wraps in try/catch.
    #>
    param(
        [Parameter(Mandatory)]$Dte,
        [int]$MaxAttempts = 12,
        [int]$DelayMs = 250
    )
    if ($null -eq $Dte.Solution -or $Dte.Solution.Projects.Count -eq 0) { return }

    $sm = $null
    foreach ($proj in $Dte.Solution.Projects) {
        $obj = $null
        try { $obj = $proj.Object } catch { continue }
        if ($null -eq $obj) { continue }
        try {
            $obj.LookupTreeItem('TIPC') | Out-Null
            $sm = $obj
            break
        } catch { continue }
    }
    if ($null -eq $sm) { return }

    $tipc = Invoke-WithComRetry { $sm.LookupTreeItem('TIPC') }
    $plcNames = @()
    for ($i = 1; $i -le $tipc.ChildCount; $i++) {
        $plcNames += (Invoke-WithComRetry { $tipc.Child($i) }).Name
    }
    foreach ($plcName in $plcNames) {
        $projectPath = "TIPC^$plcName^$plcName Project"
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            try {
                $node = Invoke-WithComRetry { $sm.LookupTreeItem($projectPath) }
                if ($null -ne $node) { break }
            } catch {
                # Source not loaded yet — wait and retry.
            }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

function Get-TcSysManagers {
    <#
    .SYNOPSIS
        Return every ITcSysManager in the loaded solution.

    .DESCRIPTION
        A sln may host more than one TwinCAT project; each TwinCAT project
        exposes its own ITcSysManager via its EnvDTE Project.Object. The
        wizard "Add → New Project → TwinCAT XAE Project" pattern produces
        exactly this layout (two sibling .tsprojs sharing one .sln), and
        our refactored Add-TcPlcProject follows it. So callers that want
        to find a PLC by name need to iterate every sysmanager.

        Probes each EnvDTE project by trying LookupTreeItem('TIPC') and
        retries the iteration to absorb the brief window where
        Projects[i].Object is $null while XAE is finishing a mutation.
    #>
    param(
        [Parameter(Mandatory)]$Dte,
        [int]$MaxAttempts = 8,
        [int]$DelayMs = 250
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if ($Dte.Solution.Projects.Count -eq 0) {
            Start-Sleep -Milliseconds $DelayMs
            continue
        }
        $found = @()
        foreach ($proj in $Dte.Solution.Projects) {
            $obj = $null
            try { $obj = $proj.Object } catch { continue }
            if ($null -eq $obj) { continue }
            try {
                $obj.LookupTreeItem('TIPC') | Out-Null
                $found += $obj
            } catch { continue }
        }
        if ($found.Count -gt 0) { return ,$found }
        Start-Sleep -Milliseconds $DelayMs
    }
    throw 'No TwinCAT project (ITcSysManager) found in solution.'
}

function Get-TcSysManager {
    <#
    .SYNOPSIS
        Return one ITcSysManager. If -PlcName is given, return the sysmanager
        whose TIPC contains that PLC. Otherwise return the first sysmanager
        in the solution.

    .DESCRIPTION
        Sln-wide operations that don't care which .tsproj they hit (e.g.
        first-project discovery during create) pass no -PlcName. PLC-scoped
        operations pass the PLC name so we land on the right .tsproj in a
        multi-PLC sln.

        Backwards-compatible with the single-tsproj case: when only one
        sysmanager exists, calling with or without -PlcName yields the
        same object.

        ITcSmTreeItem exposes _NewEnum, so PowerShell treats it as a
        collection and unrolls on `return`; we use Write-Output -NoEnumerate
        to keep the COM object intact.
    #>
    param(
        [Parameter(Mandatory)]$Dte,
        [string]$PlcName = '',
        [int]$MaxAttempts = 8,
        [int]$DelayMs = 250
    )
    $managers = Get-TcSysManagers -Dte $Dte -MaxAttempts $MaxAttempts -DelayMs $DelayMs
    if (-not $PlcName) {
        Write-Output $managers[0] -NoEnumerate
        return
    }
    foreach ($sm in $managers) {
        try {
            $tipc = $sm.LookupTreeItem('TIPC')
            for ($i = 1; $i -le $tipc.ChildCount; $i++) {
                if ($tipc.Child($i).Name -eq $PlcName) {
                    Write-Output $sm -NoEnumerate
                    return
                }
            }
        } catch { continue }
    }
    throw "PLC project '$PlcName' not found in any TwinCAT project under the solution."
}

function Resolve-TcPlcName {
    <#
    .SYNOPSIS
        Pick the PLC project name to operate on. If $Explicit is non-empty,
        return it. Otherwise scan every TwinCAT project under the solution
        and return the unique PLC name; throw with the candidate list if
        there's zero or more than one across all .tsprojs.
    #>
    param(
        [Parameter(Mandatory)]$Dte,
        [string]$Explicit = ''
    )
    if ($Explicit) { return $Explicit }
    $managers = Get-TcSysManagers -Dte $Dte
    $names = @()
    foreach ($sm in $managers) {
        try {
            $tipc = $sm.LookupTreeItem('TIPC')
            for ($i = 1; $i -le $tipc.ChildCount; $i++) {
                $names += $tipc.Child($i).Name
            }
        } catch { continue }
    }
    if ($names.Count -eq 0) {
        throw 'No PLC projects under TIPC. Add one (or pass -PlcName explicitly).'
    }
    if ($names.Count -gt 1) {
        throw "Multiple PLC projects in solution ($($names -join ', ')). Pass -PlcName to disambiguate."
    }
    return $names[0]
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
    if ($Declaration) { $Item.DeclarationText = $Declaration }
    # GVL tree items don't expose ImplementationText — they're declaration-
    # only. Skipping the empty-string assignment lets GVL writes round-trip
    # cleanly. FBs/methods/etc. with a real body still write fine.
    if ($Implementation) { $Item.ImplementationText = $Implementation }
}

function Get-TcItemSource {
    <#
    .SYNOPSIS
        Read declaration and implementation text from a tree item.

    .DESCRIPTION
        Mirror of Set-TcItemSource. Returns a hashtable with the two
        text blocks plus a combined Code field joined by a newline.
        Methods/actions and the FB-level item both expose
        DeclarationText / ImplementationText through COM dispatch.

    .OUTPUTS
        @{ declaration = string; implementation = string; code = string }
    #>
    param([Parameter(Mandatory)]$Item)

    $decl = ''
    $impl = ''
    try { $decl = [string]$Item.DeclarationText } catch { $decl = '' }
    try { $impl = [string]$Item.ImplementationText } catch { $impl = '' }
    $code = if ($impl) { "$decl`n$impl" } else { $decl }
    return @{ declaration = $decl; implementation = $impl; code = $code }
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
    Get-TcKind, Get-TcDte, Open-TcSolution, Get-TcSysManager, Get-TcSysManagers, `
    Resolve-TcPlcName, Get-TcPlcProjectNode, Get-TcPousFolder, Find-TcChild, `
    Set-TcItemSource, Get-TcItemSource, Split-TcCode, Find-Devenv, `
    Invoke-TcDevenvBuild, Read-TcBuildLog, `
    Invoke-WithComRetry, Wait-TcPlcProjectsLoaded, Save-TcSolution
