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
    Property             = 611
    InterfaceProperty    = 612
    PropertyGet          = 613
    PropertySet          = 614
    GVL                  = 615
    Transition           = 616
    Interface            = 618
    InterfacePropertyGet = 654
    InterfacePropertySet = 655
    PlcProject           = 0  # special: passed with template name in 4th arg
}

function Get-TcKind {
    <#
    .SYNOPSIS
        Map a logical type name to its CreateChild kind constant.

        Accepts: function_block, function, program, interface, method, action,
        interface_method, property, interface_property, property_get,
        property_set, interface_property_get, interface_property_set, gvl,
        struct, enum, union, folder.
    #>
    param([Parameter(Mandatory)][string]$Type)
    switch ($Type.ToLowerInvariant()) {
        'function_block' { return $script:TcKind.FunctionBlock }
        'function'       { return $script:TcKind.Function }
        'program'        { return $script:TcKind.Program }
        'interface'      { return $script:TcKind.Interface }
        'method'             { return $script:TcKind.Method }
        'interface_method'   { return $script:TcKind.InterfaceMethod }
        'action'             { return $script:TcKind.Action }
        'property'           { return $script:TcKind.Property }
        'interface_property' { return $script:TcKind.InterfaceProperty }
        'property_get'           { return $script:TcKind.PropertyGet }
        'property_set'           { return $script:TcKind.PropertySet }
        'interface_property_get' { return $script:TcKind.InterfacePropertyGet }
        'interface_property_set' { return $script:TcKind.InterfacePropertySet }
        'gvl'            { return $script:TcKind.GVL }
        'struct'         { return $script:TcKind.Struct }
        'enum'           { return $script:TcKind.Enum }
        'union'          { return $script:TcKind.Union }
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

function Use-TcSolution {
    <#
    .SYNOPSIS
        Resolve the solution to operate on: open an explicit path when one
        is given, otherwise use the solution already open in the attached
        instance.

    .DESCRIPTION
        TcKit's default model is "operate on whatever solution is open in
        the attached TcXaeShell". The operator (or a one-off open_project)
        chooses it and every subsequent call follows. An explicit -Path is
        only needed for a headless spawn or to switch solutions on purpose;
        passing one on every edit is what used to yank the IDE to a stale
        configured path. So: with a -Path, defer to Open-TcSolution
        (idempotent); with an empty -Path, require a solution to already be
        loaded and return it, raising a clear, actionable error otherwise.
    #>
    param(
        [Parameter(Mandatory)]$Dte,
        [string]$Path = ''
    )
    if ($Path) {
        return Open-TcSolution -Dte $Dte -Path $Path
    }
    $current = ''
    try { $current = $Dte.Solution.FullName } catch { }
    if (-not $current) {
        throw 'No solution is open in TcXaeShell. Open your project in XAE (or call open_project) before this operation, or pass an explicit project path.'
    }
    # An existing sln can have its PLC source trees lazy-loaded; force
    # them in so downstream LookupTreeItem calls resolve (same rationale
    # as Open-TcSolution after a fresh open).
    try { Wait-TcPlcProjectsLoaded -Dte $Dte | Out-Null } catch { }
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
    if (-not $PlcName) {
        $managers = Get-TcSysManagers -Dte $Dte -MaxAttempts $MaxAttempts -DelayMs $DelayMs
        Write-Output $managers[0] -NoEnumerate
        return
    }
    # When looking up by PLC name, retry the whole enumeration on each
    # attempt. Get-TcSysManagers returns as soon as ANY sysmanager is
    # exposed, so a snapshot mid-mutation can see only one of the two
    # projects in a multi-tsproj sln and miss the PLC we want.
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $managers = @()
        try { $managers = Get-TcSysManagers -Dte $Dte -MaxAttempts 1 -DelayMs 0 } catch { }
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
        Start-Sleep -Milliseconds $DelayMs
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

function Get-TcPlcSysNode {
    <#
    .SYNOPSIS
        Return the system-level PLC tree item at TIPC^<plc>. This node
        exposes ITcPlcProject — boot-project / activate operations
        (BootProjectAutostart, GenerateBootProject) live here.

    .DESCRIPTION
        Distinct from Get-TcPlcProjectNode, which returns the IDE-level
        node at TIPC^<plc>^<plc> Project (ITcPlcIECProject) for
        source-tree authoring.

        Mistaking these two will quietly fail because the interfaces
        don't overlap: ITcPlcIECProject doesn't expose
        BootProjectAutostart, and ITcPlcProject has no POUs folder. Use
        Get-TcPlcSysNode for runtime/boot ops, Get-TcPlcProjectNode for
        source ops.
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName")
    Write-Output $node -NoEnumerate
}

function Get-TcPlcProjectNode {
    <#
    .SYNOPSIS
        Return the IDE-level PLC project tree item at
        TIPC^<plc>^<plc> Project. This node exposes ITcPlcIECProject —
        source items (POUs, GVLs, DUTs) live underneath.

    .DESCRIPTION
        Distinct from Get-TcPlcSysNode, which returns the system-level
        node at TIPC^<plc> (ITcPlcProject) for boot/activate ops. See
        the doc-block on Get-TcPlcSysNode for the distinction.
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName^$PlcName Project")
    Write-Output $node -NoEnumerate
}

# ------------------------------------------------------------------
# .plcproj file editing (pure XML manipulation, no DTE/COM)
# ------------------------------------------------------------------

function Find-TcPlcProjFile {
    <#
    .SYNOPSIS
        Locate the .plcproj file for a named PLC project under a solution
        directory.

    .DESCRIPTION
        The on-disk layout varies (Add-TcPlcProject creates a TwinCAT
        wrapper folder per .tsproj, with the PLC project's source folder
        nested inside), but each PLC project's source folder always
        contains exactly one file called <PlcName>.plcproj. Recursive
        search from the sln directory finds it reliably; throws if zero
        or more than one match is found.
    #>
    param(
        [Parameter(Mandatory)][string]$SolutionPath,
        [Parameter(Mandatory)][string]$PlcName
    )
    $slnDir = [System.IO.Path]::GetDirectoryName($SolutionPath)
    if (-not (Test-Path -LiteralPath $slnDir)) {
        throw "Solution directory not found: $slnDir"
    }
    $candidates = @(
        Get-ChildItem -Path $slnDir -Filter "$PlcName.plcproj" `
                      -Recurse -File -ErrorAction SilentlyContinue
    )
    if ($candidates.Count -eq 0) {
        throw "No .plcproj file found for PLC '$PlcName' under $slnDir."
    }
    if ($candidates.Count -gt 1) {
        $paths = ($candidates | ForEach-Object { $_.FullName }) -join ', '
        throw "Multiple .plcproj files match PLC name '$PlcName' under ${slnDir}: $paths"
    }
    return $candidates[0].FullName
}

function Test-TcPlcProjHasPlaceholder {
    <#
    .SYNOPSIS
        File-only check: does $PlcProjPath already declare a
        <PlaceholderReference Include="$PlaceholderName"> element?

    .DESCRIPTION
        Mirrors the XPath probe at the top of
        Set-TcPlcProjPlaceholderParameters. Lets
        Add-TcLibraryPlaceholder.ps1 short-circuit the COM AddPlaceholder
        call (which throws "already contained!" on a duplicate) and fall
        straight through to the parameter splice. See ADR-0011.

    .OUTPUTS
        $true if the placeholder is present; $false otherwise.
        Returns $false on a missing file (caller surfaces the real
        error via the COM path).
    #>
    param(
        [Parameter(Mandatory)][string]$PlcProjPath,
        [Parameter(Mandatory)][string]$PlaceholderName
    )

    if (-not (Test-Path -LiteralPath $PlcProjPath)) {
        return $false
    }

    try {
        [xml]$doc = Get-Content -LiteralPath $PlcProjPath -Raw -ErrorAction Stop
    } catch {
        return $false
    }

    $defaultNs = $doc.DocumentElement.NamespaceURI
    $nsMgr = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    if ($defaultNs) {
        $nsMgr.AddNamespace('m', $defaultNs)
        $xpath = "//m:PlaceholderReference[@Include='$PlaceholderName']"
    } else {
        $xpath = "//PlaceholderReference[@Include='$PlaceholderName']"
    }

    $node = $doc.SelectSingleNode($xpath, $nsMgr)
    return ($null -ne $node)
}

function Set-TcPlcProjPlaceholderParameters {
    <#
    .SYNOPSIS
        Splice or replace a <Parameters> override block under a named
        <PlaceholderReference Include="..."> in a .plcproj file.

    .DESCRIPTION
        TwinCAT's "Library Parameters" dialog writes overrides into the
        consumer .plcproj in this exact shape:

            <PlaceholderReference Include="TcUnit">
              <DefaultResolution>...</DefaultResolution>
              <Namespace>TcUnit</Namespace>
              <Parameters>
                <Parameter ListName="GVL_PARAM_TCUNIT" xmlns="">
                  <Key>XUNITENABLEPUBLISH</Key>
                  <Value>TRUE</Value>
                </Parameter>
              </Parameters>
            </PlaceholderReference>

        The schema is specific and was reverse-engineered off the IDE's
        own output; nothing else makes the runtime honour the override:

          - Wrapper element is <Parameters> (plural) in the MSBuild
            namespace.
          - Each <Parameter> child resets to the empty namespace via
            xmlns="", so <Key>/<Value> aren't in the MSBuild namespace.
          - The ListName attribute carries the host parameter list's GVL
            name UPPERCASED (e.g. "GVL_Param_TcUnit" -> "GVL_PARAM_TCUNIT").
          - <Key> carries the parameter identifier UPPERCASED; <Value>
            carries the value verbatim, so TwinCAT booleans need
            "TRUE"/"FALSE".
          - One <Parameter> element per (ListName, Key) pair; siblings
            stack inside the same <Parameters> wrapper.

        The Automation Interface has no Set/ChangeParameter equivalent on
        ITcPlcLibraryManager or ITcPlcPlaceholderRef, and the placeholder
        tree item's ConsumeXml schema for these overrides is undocumented.
        Writing the on-disk MSBuild XML directly is the only reliable
        path. The caller is responsible for closing the solution before
        this call and reopening after, so the DTE's in-memory model picks
        the change up before the next File.SaveAll can regenerate the
        file from a stale tree.

        This function is file-only (no DTE/COM), which makes it unit-
        testable without a live bridge.

        Idempotent: existing <Parameter> elements with matching
        (ListName, Key) attribute pairs are replaced; new ones are
        appended; the <Parameters> wrapper is reused if already present.

    .PARAMETER PlcProjPath
        Absolute path to the consumer .plcproj file.

    .PARAMETER PlaceholderName
        Value of <PlaceholderReference Include="...">. Throws if no
        matching placeholder is found.

    .PARAMETER Parameters
        Nested hashtable @{ ListName -> @{ Key -> Value } }. Both
        ListName and Key are uppercased on disk regardless of the casing
        passed here; Value is written verbatim.
    #>
    param(
        [Parameter(Mandatory)][string]$PlcProjPath,
        [Parameter(Mandatory)][string]$PlaceholderName,
        [Parameter(Mandatory)][hashtable]$Parameters
    )

    if (-not (Test-Path -LiteralPath $PlcProjPath)) {
        throw "PLC project file not found: $PlcProjPath"
    }
    [xml]$doc = Get-Content -LiteralPath $PlcProjPath -Raw -ErrorAction Stop
    $defaultNs = $doc.DocumentElement.NamespaceURI
    $nsMgr = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    if ($defaultNs) {
        $nsMgr.AddNamespace('m', $defaultNs)
        $placeholderXPath = "//m:PlaceholderReference[@Include='$PlaceholderName']"
        $wrapperXPath     = 'm:Parameters'
    } else {
        $placeholderXPath = "//PlaceholderReference[@Include='$PlaceholderName']"
        $wrapperXPath     = 'Parameters'
    }
    $placeholder = $doc.SelectSingleNode($placeholderXPath, $nsMgr)
    if ($null -eq $placeholder) {
        throw "PlaceholderReference '$PlaceholderName' not found in $PlcProjPath."
    }

    $wrapperNode = $placeholder.SelectSingleNode($wrapperXPath, $nsMgr)
    if ($null -eq $wrapperNode) {
        $wrapperNode = $doc.CreateElement('Parameters', $defaultNs)
        [void]$placeholder.AppendChild($wrapperNode)
    }

    foreach ($listEntry in $Parameters.GetEnumerator()) {
        $listName = ([string]$listEntry.Key).ToUpperInvariant()
        $keys = $listEntry.Value
        if ($keys -isnot [System.Collections.IDictionary]) {
            throw "Parameters['$($listEntry.Key)'] must be a hashtable of key -> value; got $($keys.GetType().FullName)."
        }
        foreach ($kvp in $keys.GetEnumerator()) {
            $key   = ([string]$kvp.Key).ToUpperInvariant()
            $value = [string]$kvp.Value

            # Each <Parameter> child sits in the empty namespace ("xmlns=''")
            # while its <Parameters> parent is in the MSBuild namespace, so
            # XPath against the wrapper's children needs the local-name()
            # axis — a namespace-prefixed match would miss the empty-ns
            # children entirely.
            $existing = $null
            foreach ($cand in $wrapperNode.ChildNodes) {
                if ($cand.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                if ($cand.LocalName -ne 'Parameter') { continue }
                if ($cand.GetAttribute('ListName') -ne $listName) { continue }
                $keyChild = $null
                foreach ($childOfCand in $cand.ChildNodes) {
                    if ($childOfCand.NodeType -eq [System.Xml.XmlNodeType]::Element -and
                        $childOfCand.LocalName -eq 'Key') {
                        $keyChild = $childOfCand
                        break
                    }
                }
                if ($null -ne $keyChild -and $keyChild.InnerText -eq $key) {
                    $existing = $cand
                    break
                }
            }
            if ($null -ne $existing) { [void]$wrapperNode.RemoveChild($existing) }

            $paramElem = $doc.CreateElement('Parameter', '')
            $paramElem.SetAttribute('ListName', $listName)
            $keyElem = $doc.CreateElement('Key', '')
            $keyElem.InnerText = $key
            $valueElem = $doc.CreateElement('Value', '')
            $valueElem.InnerText = $value
            [void]$paramElem.AppendChild($keyElem)
            [void]$paramElem.AppendChild($valueElem)
            [void]$wrapperNode.AppendChild($paramElem)
        }
    }

    $doc.Save($PlcProjPath)
}

function Get-TcPousFolder {
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName^$PlcName Project^POUs")
    Write-Output $node -NoEnumerate
}

function Get-TcDutsFolder {
    <#
    .SYNOPSIS
        Return the DUTs folder under a PLC project.

    .DESCRIPTION
        Mirror of Get-TcPousFolder, but for the parallel DUTs folder.
        Tree path is ``TIPC^<plc>^<plc> Project^DUTs``; see
        scripts/SPIKE_NOTES.md section "Tree navigation into PLC source".
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)][string]$PlcName
    )
    $node = $SysManager.LookupTreeItem("TIPC^$PlcName^$PlcName Project^DUTs")
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

function Resolve-TcFolderPath {
    <#
    .SYNOPSIS
        Walk a slash-separated tree path under a root, returning the leaf
        tree item.

    .DESCRIPTION
        Splits $Path on '/' or '\' and performs direct-child lookups
        under $Root segment by segment. Throws with a precise error if
        any segment is missing. An empty $Path returns $Root unchanged
        so callers can pass it through.

        Kind is intentionally not validated during traversal: the
        well-known top-level subtrees (POUs, DUTs, References) don't
        all carry ItemType=601, but they're the natural starting
        point for "POUs/Drives/Motors"-style paths. The downstream
        CreateChild call will fail loud if the resolved parent doesn't
        accept the requested child kind. (XAE carries the kind on
        ItemType; ItemSubType is reserved for I/O sub-discrimination
        and is 0 on PLC source items.)

        Tree path conventions are documented at
        https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html;
        Beckhoff's CreatePlcFolder helper in TC_AI_DOTNET_Samples
        GeneratePlcProject.cs is the canonical creation pattern.
    #>
    param(
        [Parameter(Mandatory)]$Root,
        [string]$Path = ''
    )
    if (-not $Path) { Write-Output $Root -NoEnumerate; return }
    $cursor = $Root
    foreach ($seg in ($Path -split '[/\\]')) {
        if (-not $seg) { continue }
        $next = $null
        for ($i = 1; $i -le $cursor.ChildCount; $i++) {
            $child = $cursor.Child($i)
            if ($child.Name -eq $seg) { $next = $child; break }
        }
        if ($null -eq $next) {
            throw "Path segment '$seg' not found under '$($cursor.PathName)'."
        }
        $cursor = $next
    }
    Write-Output $cursor -NoEnumerate
}

function Remove-TcTreeItem {
    <#
    .SYNOPSIS
        Delete a tree item by resolving its parent via PathName and calling
        ITcSmTreeItem::DeleteChild on the parent.

    .DESCRIPTION
        The Automation Interface deletion primitive is
        DeleteChild(BSTR bstrName) on the parent item (single arg, by
        display name; see https://infosys.beckhoff.com/content/1033/
        tc3_automationinterface/242837387.html). Items returned by
        recursive name lookups don't carry a Parent property, so we
        derive the parent path by stripping the last segment of the
        item's PathName and re-resolving via LookupTreeItem. This works
        uniformly for POUs (in folders or at root), GVLs, DUTs, methods/
        properties (whose parent is the POU), and folders.
    #>
    param(
        [Parameter(Mandatory)]$SysManager,
        [Parameter(Mandatory)]$Item
    )
    $pathSegments = $Item.PathName -split '\^'
    if ($pathSegments.Count -lt 2) {
        throw "Cannot resolve parent of '$($Item.Name)' (PathName=$($Item.PathName))."
    }
    $parentPath = ($pathSegments[0..($pathSegments.Count - 2)]) -join '^'
    $parent = $SysManager.LookupTreeItem($parentPath)
    $parent.DeleteChild($Item.Name)
    return $parentPath
}

# ------------------------------------------------------------------
# Source code write
# ------------------------------------------------------------------

function Split-TcCode {
    <#
    .SYNOPSIS
        Split combined ST source into a declaration block and an implementation
        body.

    .DESCRIPTION
        Strategy, in order:
        1. If at least one END_VAR is present, split at the last END_VAR — the
           declaration ends there and the body follows.
        2. Otherwise, if a POU/method header keyword (METHOD / FUNCTION_BLOCK /
           FUNCTION / PROGRAM / INTERFACE / PROPERTY / ACTION / VAR_GLOBAL) is
           present, split immediately after the last such header line. The
           header stays in the declaration; everything after is body.
        3. Otherwise, treat the whole input as implementation with an empty
           declaration. This is the right default for "body only" callers.

        Step 2 fixes #84: a method like ``METHOD Step : INT\nCASE state OF...``
        used to land entirely in DeclarationText (because no END_VAR was
        present), which silently broke compilation. The empty ``VAR/END_VAR``
        workaround was a way to force step 1 to fire; with the header-line
        fallback in place that workaround is no longer needed.

    .OUTPUTS
        @{ declaration = string; implementation = string }
    #>
    param([Parameter(Mandatory)][string]$Code)

    $code = $Code -replace "`r`n", "`n"

    $endVarMatches = [regex]::Matches($code, '(?m)^[ \t]*END_VAR[ \t]*$')
    if ($endVarMatches.Count -gt 0) {
        $last = $endVarMatches[$endVarMatches.Count - 1]
        $cut = $last.Index + $last.Length
        $decl = $code.Substring(0, $cut)
        $impl = if ($cut -lt $code.Length) { $code.Substring($cut).TrimStart("`n", "`r", " ", "`t") } else { '' }
        return @{ declaration = $decl; implementation = $impl }
    }

    $headerPattern = '(?m)^[ \t]*(METHOD|FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE|PROPERTY|ACTION|VAR_GLOBAL)\b[^\n]*$'
    $headerMatches = [regex]::Matches($code, $headerPattern)
    if ($headerMatches.Count -gt 0) {
        $lastHeader = $headerMatches[$headerMatches.Count - 1]
        $cut = $lastHeader.Index + $lastHeader.Length
        $decl = $code.Substring(0, $cut)
        $impl = if ($cut -lt $code.Length) { $code.Substring($cut).TrimStart("`n", "`r", " ", "`t") } else { '' }
        return @{ declaration = $decl; implementation = $impl }
    }

    return @{ declaration = ''; implementation = $code }
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

function Test-TcInterfacePou {
    <#
    .SYNOPSIS
        Return $true when a POU tree item's declaration is an INTERFACE.

    .DESCRIPTION
        Methods and properties added under an INTERFACE parent must be
        created with the InterfaceMethod / InterfaceProperty kind, not
        the regular Method / Property kind — XAE rejects CreateChild
        otherwise with "Cannot insert '<name>' below '<interface>'".

        Detection is done on the declaration text rather than a COM
        property because tree items expose no clean "is interface" flag.
        Block comments, line comments and attribute pragmas are stripped
        so the first surviving POU keyword wins.
    #>
    param([Parameter(Mandatory)]$Item)

    $decl = ''
    try { $decl = [string]$Item.DeclarationText } catch { return $false }
    if (-not $decl) { return $false }

    $stripped = $decl
    $stripped = [regex]::Replace($stripped, '\(\*[\s\S]*?\*\)', ' ')
    $stripped = [regex]::Replace($stripped, '//[^\r\n]*',       ' ')
    $stripped = [regex]::Replace($stripped, '\{[^}]*\}',        ' ')

    $m = [regex]::Match($stripped, '(?im)\b(FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE)\b')
    return $m.Success -and $m.Groups[1].Value.ToUpperInvariant() -eq 'INTERFACE'
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
    Get-TcKind, Get-TcDte, Open-TcSolution, Use-TcSolution, Get-TcSysManager, Get-TcSysManagers, `
    Resolve-TcPlcName, Get-TcPlcSysNode, Get-TcPlcProjectNode, Get-TcPousFolder, `
    Get-TcDutsFolder, `
    Find-TcChild, Resolve-TcFolderPath, Remove-TcTreeItem, Set-TcItemSource, Get-TcItemSource, Test-TcInterfacePou, Split-TcCode, Find-Devenv, `
    Invoke-TcDevenvBuild, Read-TcBuildLog, `
    Invoke-WithComRetry, Wait-TcPlcProjectsLoaded, Save-TcSolution, `
    Find-TcPlcProjFile, Set-TcPlcProjPlaceholderParameters, `
    Test-TcPlcProjHasPlaceholder
