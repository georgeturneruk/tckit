using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>One member of a POU with its declaration parsed and its body masked, ready for rules.</summary>
public sealed record AnalysedMember
{
    public required PouMember Source { get; init; }

    public required StDeclaration Declaration { get; init; }

    public required string MaskedBody { get; init; }
}

/// <summary>A POU with everything the rules need, parsed once.</summary>
public sealed record AnalysedPou
{
    public required string PlcName { get; init; }

    public required PouSource Source { get; init; }

    public required StDeclaration Declaration { get; init; }

    public required IReadOnlyList<AnalysedMember> Members { get; init; }

    public required string MaskedBody { get; init; }

    /// <summary>
    /// Every body in this POU joined together: the scope in which an object-level variable could
    /// legitimately be referenced. Precomputed because the unused-declaration rules scan it once
    /// per variable.
    /// </summary>
    public required string AllBodies { get; init; }

    public string Name => Source.PouName;
}

/// <summary>A GVL with its declaration parsed.</summary>
public sealed record AnalysedGvl
{
    public required string PlcName { get; init; }

    public required Gvl Source { get; init; }

    public required StDeclaration Declaration { get; init; }
}

/// <summary>A DUT with its members parsed.</summary>
public sealed record AnalysedDut
{
    public required string PlcName { get; init; }

    public required Dut Source { get; init; }

    public required StTypeDeclaration Declaration { get; init; }
}

/// <summary>
/// Everything read from disk for one analysis run. Cross-file rules ("this global is written from
/// two POUs", "this POU is never used") need the whole project in hand at once, which is what
/// separates this analyser from the per-file validators.
/// </summary>
public sealed record AnalysedProject
{
    public required ProjectStructure Structure { get; init; }

    public required TypeClassifier Classifier { get; init; }

    public IReadOnlyList<AnalysedPou> Pous { get; init; } = [];

    public IReadOnlyList<AnalysedGvl> Gvls { get; init; } = [];

    public IReadOnlyList<AnalysedDut> Duts { get; init; } = [];

    /// <summary>
    /// False when the run was scoped to a single object, which means cross-file rules cannot see
    /// enough of the project to be trusted and are skipped rather than run on partial data.
    /// </summary>
    public bool IsWholeProject { get; init; } = true;

    /// <summary>
    /// Whether any POU is written in Ladder, FBD, SFC, CFC or IL. Only ST is stored as source
    /// text, so those bodies are invisible to us: a call made from a ladder network reads as no
    /// call at all. Reachability cannot be trusted on such a project.
    /// </summary>
    public bool HasUnreadableBodies => Pous.Any(pou => pou.Source.HasUnreadableBody);

    /// <summary>Build the parsed model for one POU.</summary>
    public static AnalysedPou Analyse(PouSource source, string plcName)
    {
        ArgumentNullException.ThrowIfNull(source);

        var members = source.Members.Select(member => new AnalysedMember
        {
            Source = member,
            Declaration = DeclarationParser.Parse(member.Declaration),
            MaskedBody = StSource.Mask(member.Body),
        }).ToList();

        var maskedBody = StSource.Mask(source.Body);

        return new AnalysedPou
        {
            PlcName = plcName,
            Source = source,
            Declaration = DeclarationParser.Parse(source.Declaration),
            Members = members,
            MaskedBody = maskedBody,
            AllBodies = string.Join('\n', members.Select(m => m.MaskedBody).Prepend(maskedBody)),
        };
    }
}
