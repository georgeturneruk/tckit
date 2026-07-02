namespace TcKit.Core.Models;

/// <summary>The kind of a POU, mirroring the TwinCAT object type.</summary>
public enum PouType
{
    FunctionBlock,
    Function,
    Program,
    Interface,
}

/// <summary>The kind discriminator for a Data Unit Type.</summary>
public enum DutKind
{
    Struct,
    Enum,
    Union,
    Alias,
}

/// <summary>A POU entry in a project structure listing (metadata only, no code).</summary>
public sealed record PouRef
{
    public required string Name { get; init; }
    public required PouType PouType { get; init; }
    public required string Path { get; init; }

    /// <summary>The PLC project (TIPC child) this POU belongs to.</summary>
    public required string PlcName { get; init; }

    /// <summary>Path relative to the PLC project root, forward-slash, e.g. "POUs/Functions". Empty at root.</summary>
    public string Folder { get; init; } = "";
}

/// <summary>A GVL entry in a project structure listing.</summary>
public sealed record GvlRef
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string PlcName { get; init; }
    public string Folder { get; init; } = "";
}

/// <summary>A DUT entry in a project structure listing; <see cref="DutKind"/> lets callers prefilter without re-parsing.</summary>
public sealed record DutRef
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string PlcName { get; init; }
    public required DutKind DutKind { get; init; }
    public string Folder { get; init; } = "";
}

/// <summary>A PLC task: cycle time in microseconds, priority, and bound programs.</summary>
public sealed record TaskInfo
{
    public required string Name { get; init; }
    public int? CycleTimeUs { get; init; }
    public int? Priority { get; init; }
    public IReadOnlyList<string> Programs { get; init; } = [];
}

/// <summary>A library reference declared in a .plcproj.</summary>
public sealed record LibraryRef
{
    public required string Name { get; init; }
    public string Version { get; init; } = "";

    /// <summary>The placeholder name (e.g. "Tc2_Standard") for placeholder refs; null for direct refs.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>One PLC project (.plcproj) within a solution. Tasks live at the solution level, not here.</summary>
public sealed record PlcSection
{
    public required string Name { get; init; }
    public required string PlcprojPath { get; init; }
    public IReadOnlyList<PouRef> Pous { get; init; } = [];
    public IReadOnlyList<GvlRef> Gvls { get; init; } = [];
    public IReadOnlyList<DutRef> Duts { get; init; } = [];
    public IReadOnlyList<LibraryRef> Libraries { get; init; } = [];
}

/// <summary>
/// A solution's project map, keyed by PLC-project name. A multi-project sln returns
/// one entry per .plcproj; a single-project sln returns a one-entry map.
/// </summary>
public sealed record ProjectStructure
{
    public required string ProjectPath { get; init; }

    /// <summary>Absolute path to the resolved .sln; empty when the project has no .sln.</summary>
    public string SolutionPath { get; init; } = "";

    public IReadOnlyDictionary<string, PlcSection> Plcs { get; init; }
        = new Dictionary<string, PlcSection>();

    public IReadOnlyList<TaskInfo> Tasks { get; init; } = [];
}
