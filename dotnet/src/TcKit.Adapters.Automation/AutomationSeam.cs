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
}

/// <summary>One TwinCAT project's system manager: resolves tree items by '^'-delimited path.</summary>
internal interface ITcSysManager
{
    /// <summary>Resolve a tree item by path (e.g. "TIPC^Plc^Plc Project^POUs"); throws if absent.</summary>
    ITcTreeItem LookupTreeItem(string path);
}

/// <summary>A connection to the IDE: the open solution and its system managers.</summary>
internal interface ITcSession : IDisposable
{
    /// <summary>Open the solution at <paramref name="path"/>, or require one already open when empty.</summary>
    void UseSolution(string path);

    /// <summary>Every system manager (one per TwinCAT project) in the open solution.</summary>
    IReadOnlyList<ITcSysManager> GetSysManagers();

    /// <summary>Flush the solution to disk (File.SaveAll).</summary>
    void Save();
}
