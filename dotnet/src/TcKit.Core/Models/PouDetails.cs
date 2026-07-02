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
