namespace TcKit.Adapters.Automation;

/// <summary>
/// A tree item in the TwinCAT Automation Interface (the subset TcKit authoring uses: ITcSmTreeItem
/// + the PLC declaration/implementation text interfaces). This is the seam that lets the authoring
/// logic be exercised against an in-memory fake in CI instead of only against a live XAE over COM.
/// </summary>
internal interface ITcTreeItem
{
    string Name { get; }

    /// <summary>The '^'-delimited tree path of this item.</summary>
    string PathName { get; }

    /// <summary>The TwinCAT kind constant (e.g. 604 for a function block); carried on ItemType.</summary>
    int ItemType { get; }

    int ChildCount { get; }

    /// <summary>FB-level / item declaration text. Setting it on an item with no declaration throws.</summary>
    string DeclarationText { get; set; }

    /// <summary>Implementation (body) text. Setting it on a declaration-only item (GVL/DUT) throws.</summary>
    string ImplementationText { get; set; }

    /// <summary>1-based child accessor (matches the COM collection convention).</summary>
    ITcTreeItem Child(int index);

    /// <summary>Create a child of the given TwinCAT kind. vInfo carries property metadata (else null).</summary>
    ITcTreeItem CreateChild(string name, int kind, object? before, object? vInfo);

    void DeleteChild(string name);

    // --- library manager + IEC project surface ------------------------------
    // These are dispatched (late-bound over COM) on specific nodes: AddLibrary /
    // AddPlaceholder / RemoveReference on the References node (ITcPlcLibraryManager),
    // and ProduceXml / ConsumeXml / SaveAsLibrary / CheckAllObjects on the PLC
    // project node (ITcPlcIECProject). Nodes that don't model an operation throw.

    /// <summary>Serialise this item to its TwinCAT XML form (flags 0 == non-recursive).</summary>
    string ProduceXml(int flags);

    /// <summary>Apply a TwinCAT XML fragment to this item (the documented metadata round-trip).</summary>
    void ConsumeXml(string xml);

    /// <summary>Add a library reference (ITcPlcLibraryManager.AddLibrary). References node only.</summary>
    void AddLibrary(string name, string version, string distributor);

    /// <summary>Add a library placeholder (ITcPlcLibraryManager.AddPlaceholder). References node only.</summary>
    void AddPlaceholder(string name, string defaultLibrary, string version, string distributor);

    /// <summary>Remove a placeholder by name (single-arg RemoveReference). References node only.</summary>
    void RemoveReference(string name);

    /// <summary>Remove a library reference by identity (3-arg RemoveReference). References node only.</summary>
    void RemoveReference(string name, string version, string distributor);

    /// <summary>Save the PLC project as a .library, optionally installing it. PLC project node only.</summary>
    void SaveAsLibrary(string outputPath, bool install);

    /// <summary>Run an in-process compile (forces placeholder resolution); true on a clean compile. PLC project node only.</summary>
    bool CheckAllObjects();

    /// <summary>Autostart the boot project on the system-level PLC node (TIPC^plc). ITcPlcProject only.</summary>
    bool BootProjectAutostart { get; set; }

    /// <summary>Regenerate the boot project (true = activate). System-level PLC node only.</summary>
    void GenerateBootProject(bool activate);
}

/// <summary>One TwinCAT project's system manager: resolves tree items by '^'-delimited path.</summary>
internal interface ITcSysManager
{
    /// <summary>The owning TwinCAT project's display name (the EnvDTE project name), used to target one
    /// project's I/O tree in a multi-project solution.</summary>
    string ProjectName { get; }

    /// <summary>Resolve a tree item by path (e.g. "TIPC^Plc^Plc Project^POUs"); throws if absent.</summary>
    ITcTreeItem LookupTreeItem(string path);

    /// <summary>Set the deploy target's AMS Net ID before ActivateConfiguration.</summary>
    void SetTargetNetId(string amsNetId);

    /// <summary>Activate the configuration on the set target (puts TwinCAT into Run, downloads the bootapp).</summary>
    void ActivateConfiguration();

    /// <summary>Persist just this TwinCAT project (EnvDTE <c>Project.Save()</c>) so I/O-tree edits hit the
    /// <c>.tsproj</c> on disk immediately, rather than waiting for a Build's Save-All.</summary>
    void Save();
}

/// <summary>One IDE Error List diagnostic row (raw; severity/code decoding happens in the builder).</summary>
internal sealed record ComErrorItem(string File, int Line, string Description, int Level, string Project);

/// <summary>A connection to the IDE: the open solution and its system managers.</summary>
internal interface ITcSession : IDisposable
{
    /// <summary>Absolute path of the open solution (empty when none / not knowable). Used for file-side scans.</summary>
    string SolutionPath { get; }

    /// <summary>Open the solution at <paramref name="path"/>, or require one already open when empty.</summary>
    void UseSolution(string path);

    /// <summary>Every system manager (one per TwinCAT project) in the open solution.</summary>
    IReadOnlyList<ITcSysManager> GetSysManagers();

    /// <summary>Flush the solution to disk (File.SaveAll).</summary>
    void Save();

    /// <summary>Create an empty solution shell (Solution.Create) at <paramref name="directory"/>.</summary>
    void CreateSolution(string directory, string name);

    /// <summary>Add a TwinCAT project from a .tsproj template into its own subdirectory.</summary>
    void AddProjectFromTemplate(string templatePath, string destinationDir, string name);

    /// <summary>Persist the solution to <paramref name="path"/> (Solution.SaveAs + File.SaveAll).</summary>
    void SaveSolutionAs(string path);

    /// <summary>Close the open solution without saving (Solution.Close(false)).</summary>
    void CloseSolution();

    /// <summary>
    /// Ensure a solution configuration is active before ActivateConfiguration (else it throws an
    /// opaque E_UNEXPECTED). Returns the resolved name; activates the sole config, or the first
    /// matching <paramref name="prefer"/> when several exist.
    /// </summary>
    string ResolveSolutionConfiguration(string prefer);

    /// <summary>
    /// Read the IDE Error List (PLC compile diagnostics). Returns null when the tool window is not
    /// exposed (e.g. TcXaeShell Express), so the caller can fall back.
    /// </summary>
    IReadOnlyList<ComErrorItem>? ReadErrorList();
}
