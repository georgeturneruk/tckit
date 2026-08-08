namespace TcKit.Core.Models;

/// <summary>A method signature on a POU interface (no body, locals stripped).</summary>
public sealed record MethodSignature
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public required string Declaration { get; init; }
}

/// <summary>A property signature on a POU interface, with accessor presence flags.</summary>
public sealed record PropertySignature
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public required string Declaration { get; init; }
    public bool HasGet { get; init; } = true;
    public bool HasSet { get; init; }
}

/// <summary>The interface of a POU: declaration plus method/property signatures and action names, no bodies.</summary>
public sealed record PouInterface
{
    public required string PouName { get; init; }
    public required PouType PouType { get; init; }
    public required string Declaration { get; init; }
    public IReadOnlyList<MethodSignature> Methods { get; init; } = [];
    public IReadOnlyList<PropertySignature> Properties { get; init; } = [];
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>The FB-level declaration block of a POU only (VAR sections; no methods or bodies).</summary>
public sealed record PouDeclaration
{
    public required string PouName { get; init; }
    public required PouType PouType { get; init; }
    public required string Declaration { get; init; }
}

/// <summary>The declaration and body of a single method, action, or property accessor.</summary>
public sealed record PouItem
{
    public required string PouName { get; init; }
    public required string ItemName { get; init; }
    public required string Declaration { get; init; }
    public required string Body { get; init; }
}

/// <summary>A Global Variable List's declaration block.</summary>
public sealed record Gvl
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Declaration { get; init; }
}

/// <summary>A Data Unit Type's declaration; <see cref="BaseType"/> is the aliased type for ALIAS DUTs, empty otherwise.</summary>
public sealed record Dut
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Declaration { get; init; }
    public DutKind DutKind { get; init; } = DutKind.Struct;
    public string BaseType { get; init; } = "";
}

/// <summary>What a <see cref="PouMember"/> is. Property accessors are separate members from the property header.</summary>
public enum PouMemberKind
{
    Method,
    Action,
    Property,
    PropertyGet,
    PropertySet,
}

/// <summary>One member of a POU, with both halves of its source.</summary>
public sealed record PouMember
{
    /// <summary>The item name, using the same spelling as <c>GetPouItem</c>: "Execute", "Status", "Status.Get".</summary>
    public required string Name { get; init; }

    public required PouMemberKind Kind { get; init; }

    public required string Declaration { get; init; }

    public required string Body { get; init; }
}

/// <summary>
/// A whole POU's source: declaration, body, and every member with its own halves. The single
/// read that whole-object work (analysis, documentation) needs, so it does not have to issue one
/// call per member and re-parse the file each time.
/// </summary>
public sealed record PouSource
{
    public required string PouName { get; init; }
    public required PouType PouType { get; init; }
    public required string Path { get; init; }
    public required string Declaration { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<PouMember> Members { get; init; } = [];
}
