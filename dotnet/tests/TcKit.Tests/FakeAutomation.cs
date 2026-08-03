using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// In-memory fake of the TwinCAT Automation Interface seam. This is our executable spec of how the
/// AI behaves: CreateChild appends a typed child, declaration-only items (GVL/DUT) reject an
/// implementation write, and LookupTreeItem resolves the '^'-delimited doubled-name paths. The
/// authoring logic in ProjectAuthor is tested against this in CI, with no TwinCAT or COM.
/// </summary>
internal sealed class FakeTreeItem(string name, int kind = 0) : ITcTreeItem
{
    private readonly List<FakeTreeItem> _children = [];
    private string _declaration = "";
    private string _implementation = "";

    public string Name { get; } = name;
    public int Kind { get; } = kind;
    public object? VInfo { get; private set; }
    public FakeTreeItem? Parent { get; private set; }
    public IReadOnlyList<FakeTreeItem> Children => _children;

    // --- library/IEC-project modelling --------------------------------------
    // A reference child stamped by AddLibrary/AddPlaceholder; ProduceXml emits the
    // <Library> shape DeleteLibraryReference parses. The PLC project node (no Ref)
    // emits a <ProjectInfo> block round-tripped by SaveAsLibrary.

    private (string Version, string Distributor, string Effective, bool IsPlaceholder)? _reference;
    public string ProjectTitle { get; private set; } = "";
    public string ProjectCompany { get; private set; } = "";
    public string ProjectVersion { get; private set; } = "";
    public string? SavedLibraryPath { get; private set; }
    public bool SavedLibraryInstall { get; private set; }
    public int CheckAllObjectsCount { get; private set; }
    public bool IsPlaceholder => _reference is { IsPlaceholder: true };

    public string PathName => Parent is null ? Name : $"{Parent.PathName}^{Name}";
    public int ItemType => Kind;
    public int ChildCount => _children.Count;

    public string DeclarationText
    {
        get => _declaration;
        set => _declaration = value;
    }

    public string ImplementationText
    {
        get => _implementation;
        set
        {
            // Encodes the AI behaviour: GVLs and DUTs are declaration-only; XAE rejects a body write.
            if (Kind is TcKind.Gvl or TcKind.Struct or TcKind.Enum or TcKind.Union)
            {
                throw new InvalidOperationException($"Tree-item kind {Kind} has no implementation text.");
            }

            _implementation = value;
        }
    }

    public ITcTreeItem Child(int index) => _children[index - 1];

    public ITcTreeItem CreateChild(string name, int kind, object? before, object? vInfo)
    {
        // XAE names property accessors "Get"/"Set" regardless of the (empty) name passed in.
        if (string.IsNullOrEmpty(name))
        {
            name = kind switch
            {
                TcKind.PropertyGet or TcKind.InterfacePropertyGet => "Get",
                TcKind.PropertySet or TcKind.InterfacePropertySet => "Set",
                _ => name,
            };
        }

        var child = new FakeTreeItem(name, kind) { Parent = this, VInfo = vInfo };
        _children.Add(child);
        return child;
    }

    public void DeleteChild(string name) => _children.RemoveAll(c => c.Name == name);

    public string ProduceXml(int flags)
    {
        if (_reference is { } reference)
        {
            return "<TreeItem><Library>"
                + $"<Name>{Name}</Name>"
                + $"<Distributor>{reference.Distributor}</Distributor>"
                + $"<EffectiveVersion>{reference.Effective}</EffectiveVersion>"
                + "</Library></TreeItem>";
        }

        return "<TreeItem><ProjectInfo>"
            + $"<Title>{ProjectTitle}</Title>"
            + $"<Company>{ProjectCompany}</Company>"
            + $"<Version>{ProjectVersion}</Version>"
            + "</ProjectInfo></TreeItem>";
    }

    public void ConsumeXml(string xml)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        ProjectTitle = doc.SelectSingleNode("//Title")?.InnerText ?? ProjectTitle;
        ProjectCompany = doc.SelectSingleNode("//Company")?.InnerText ?? ProjectCompany;
        ProjectVersion = doc.SelectSingleNode("//Version")?.InnerText ?? ProjectVersion;
    }

    public void AddLibrary(string name, string version, string distributor)
    {
        if (FindDirect(name) is not null)
        {
            throw new InvalidOperationException($"Library '{name}' already contained!");
        }

        var effective = version is "*" or "" ? "1.0.0.0" : version;
        var child = new FakeTreeItem(name) { Parent = this, _reference = (version, distributor, effective, false) };
        _children.Add(child);
    }

    public void AddPlaceholder(string name, string defaultLibrary, string version, string distributor)
    {
        if (FindDirect(name) is not null)
        {
            throw new InvalidOperationException($"Placeholder '{name}' already contained!");
        }

        var effective = version is "*" or "" ? "1.0.0.0" : version;
        var child = new FakeTreeItem(name) { Parent = this, _reference = (version, distributor, effective, true) };
        _children.Add(child);
    }

    public void RemoveReference(string name)
    {
        if (_children.RemoveAll(c => c.Name == name) == 0)
        {
            throw new InvalidOperationException($"Reference '{name}' not found.");
        }
    }

    public void RemoveReference(string name, string version, string distributor)
    {
        var removed = _children.RemoveAll(c =>
            c.Name == name && c._reference is { } r && r.Distributor == distributor
            && (version == r.Effective || version == r.Version));
        if (removed == 0)
        {
            throw new InvalidOperationException($"Reference '{name}' v{version} ({distributor}) not found.");
        }
    }

    public void SaveAsLibrary(string outputPath, bool install)
    {
        SavedLibraryPath = outputPath;
        SavedLibraryInstall = install;
    }

    public bool CheckAllObjectsResult { get; set; } = true;
    public bool BootProjectAutostart { get; set; }
    public bool BootProjectGenerated { get; private set; }

    public bool CheckAllObjects()
    {
        CheckAllObjectsCount++;
        return CheckAllObjectsResult;
    }

    public void GenerateBootProject(bool activate) => BootProjectGenerated = activate;

    public FakeTreeItem Add(FakeTreeItem child)
    {
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public FakeTreeItem? FindDirect(string name) => _children.FirstOrDefault(c => c.Name == name);
}

internal sealed class FakeSysManager : ITcSysManager
{
    private readonly Dictionary<string, FakeTreeItem> _roots = new(StringComparer.Ordinal);

    /// <summary>The TIPC tree is the primary root; pass extra roots (e.g. a "TIID" I/O tree) to model them.</summary>
    public FakeSysManager(FakeTreeItem tipc, params FakeTreeItem[] extraRoots)
        : this("TwinCAT Project", tipc, extraRoots)
    {
    }

    /// <summary>Named overload to model a multi-project solution (each project targets its own I/O tree).</summary>
    public FakeSysManager(string projectName, FakeTreeItem tipc, params FakeTreeItem[] extraRoots)
    {
        ProjectName = projectName;
        Tipc = tipc;
        _roots[tipc.Name] = tipc;
        foreach (var root in extraRoots)
        {
            _roots[root.Name] = root;
        }
    }

    public FakeTreeItem Tipc { get; }

    public string ProjectName { get; }

    public string? TargetNetId { get; private set; }
    public bool Activated { get; private set; }

    /// <summary>Per-project save count (mirrors the live ComTcSysManager.Save() -> Project.Save()).</summary>
    public int SaveCount { get; private set; }

    public void SetTargetNetId(string amsNetId) => TargetNetId = amsNetId;

    public void ActivateConfiguration() => Activated = true;

    public void Save() => SaveCount++;

    public ITcTreeItem LookupTreeItem(string path)
    {
        var parts = path.Split('^');
        if (parts.Length == 0 || !_roots.TryGetValue(parts[0], out var node))
        {
            throw new InvalidOperationException($"Tree path root not found: '{path}'.");
        }

        for (var i = 1; i < parts.Length; i++)
        {
            node = node.FindDirect(parts[i])
                ?? throw new InvalidOperationException($"Tree item not found: '{path}'.");
        }

        return node;
    }
}

internal sealed class FakeSession(params ITcSysManager[] sysManagers) : ITcSession
{
    private readonly List<ITcSysManager> _sysManagers = [.. sysManagers];

    public string SolutionPath { get; set; } = "";

    public int SaveCount { get; private set; }

    public string? CreatedSolutionDir { get; private set; }
    public string? CreatedSolutionName { get; private set; }
    public string? SavedAsPath { get; private set; }
    public bool Closed { get; private set; }
    public List<string> AddedTemplates { get; } = [];

    public void UseSolution(string path)
    {
        // No-op: the fake always has its solution "open".
    }

    public IReadOnlyList<ITcSysManager> GetSysManagers() => _sysManagers;

    public void Save() => SaveCount++;

    public void CreateSolution(string directory, string name)
    {
        CreatedSolutionDir = directory;
        CreatedSolutionName = name;
        _sysManagers.Clear();
    }

    public void AddProjectFromTemplate(string templatePath, string destinationDir, string name)
    {
        AddedTemplates.Add(name);
        // A freshly-added TwinCAT project starts with an empty TIPC (no PLC yet).
        _sysManagers.Add(new FakeSysManager(new FakeTreeItem("TIPC")));
    }

    public void SaveSolutionAs(string path)
    {
        SavedAsPath = path;
        SaveCount++;
    }

    public void CloseSolution() => Closed = true;

    public string SolutionConfiguration { get; set; } = "Release";

    /// <summary>When null, ReadErrorList returns null (the "tool window not exposed" case).</summary>
    public List<ComErrorItem>? ErrorListItems { get; set; } = [];

    /// <summary>When null, ReadErrorListUia returns null (the "GUI unreachable" case).</summary>
    public List<ComErrorItem>? UiaErrorListItems { get; set; }

    /// <summary>The compileSucceeded flag the builder passed to the UIA fallback, when it did.</summary>
    public bool? UiaCompileSucceeded { get; private set; }

    public string ResolveSolutionConfiguration(string prefer) => SolutionConfiguration;

    public IReadOnlyList<ComErrorItem>? ReadErrorList() => ErrorListItems;

    public IReadOnlyList<ComErrorItem>? ReadErrorListUia(bool compileSucceeded)
    {
        UiaCompileSucceeded = compileSucceeded;
        return UiaErrorListItems;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}

/// <summary>Builds a fake TIPC tree (one sysmanager) with the standard POUs/DUTs folders per PLC.</summary>
internal static class FakeProject
{
    public static (FakeSession Session, IReadOnlyDictionary<string, FakeTreeItem> Pous,
        IReadOnlyDictionary<string, FakeTreeItem> Duts) Build(params string[] plcNames)
    {
        var (session, pous, duts, _) = BuildWithReferences(plcNames);
        return (session, pous, duts);
    }

    /// <summary>Like <see cref="Build"/> but also exposes the per-PLC References node.</summary>
    public static (FakeSession Session, IReadOnlyDictionary<string, FakeTreeItem> Pous,
        IReadOnlyDictionary<string, FakeTreeItem> Duts, IReadOnlyDictionary<string, FakeTreeItem> References)
        BuildWithReferences(params string[] plcNames)
    {
        var tipc = new FakeTreeItem("TIPC");
        var pous = new Dictionary<string, FakeTreeItem>(StringComparer.Ordinal);
        var duts = new Dictionary<string, FakeTreeItem>(StringComparer.Ordinal);
        var references = new Dictionary<string, FakeTreeItem>(StringComparer.Ordinal);

        foreach (var plc in plcNames)
        {
            var project = tipc.Add(new FakeTreeItem(plc)).Add(new FakeTreeItem($"{plc} Project"));
            pous[plc] = project.Add(new FakeTreeItem("POUs"));
            duts[plc] = project.Add(new FakeTreeItem("DUTs"));
            references[plc] = project.Add(new FakeTreeItem("References"));
        }

        return (new FakeSession(new FakeSysManager(tipc)), pous, duts, references);
    }
}
