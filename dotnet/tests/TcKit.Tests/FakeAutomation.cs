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

    public string PathName => Parent is null ? Name : $"{Parent.PathName}^{Name}";
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
        var child = new FakeTreeItem(name, kind) { Parent = this, VInfo = vInfo };
        _children.Add(child);
        return child;
    }

    public void DeleteChild(string name) => _children.RemoveAll(c => c.Name == name);

    public FakeTreeItem Add(FakeTreeItem child)
    {
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public FakeTreeItem? FindDirect(string name) => _children.FirstOrDefault(c => c.Name == name);
}

internal sealed class FakeSysManager(FakeTreeItem tipc) : ITcSysManager
{
    public FakeTreeItem Tipc { get; } = tipc;

    public ITcTreeItem LookupTreeItem(string path)
    {
        var parts = path.Split('^');
        if (parts.Length == 0 || parts[0] != Tipc.Name)
        {
            throw new InvalidOperationException($"Tree path does not start at '{Tipc.Name}': '{path}'.");
        }

        var node = Tipc;
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
    private readonly IReadOnlyList<ITcSysManager> _sysManagers = sysManagers;

    public int SaveCount { get; private set; }

    public void UseSolution(string path)
    {
        // No-op: the fake always has its solution "open".
    }

    public IReadOnlyList<ITcSysManager> GetSysManagers() => _sysManagers;

    public void Save() => SaveCount++;

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
        var tipc = new FakeTreeItem("TIPC");
        var pous = new Dictionary<string, FakeTreeItem>(StringComparer.Ordinal);
        var duts = new Dictionary<string, FakeTreeItem>(StringComparer.Ordinal);

        foreach (var plc in plcNames)
        {
            var project = tipc.Add(new FakeTreeItem(plc)).Add(new FakeTreeItem($"{plc} Project"));
            pous[plc] = project.Add(new FakeTreeItem("POUs"));
            duts[plc] = project.Add(new FakeTreeItem("DUTs"));
        }

        return (new FakeSession(new FakeSysManager(tipc)), pous, duts);
    }
}
