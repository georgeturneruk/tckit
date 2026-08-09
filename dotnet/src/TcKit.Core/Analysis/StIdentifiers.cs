using System.Text.RegularExpressions;

namespace TcKit.Core.Analysis;

/// <summary>
/// Identifier scanning over masked Structured Text. Every method expects text already put through
/// <see cref="StSource.Mask"/>, so a name mentioned in a comment or a string literal can never
/// produce a hit. Matching is case-insensitive throughout because IEC 61131-3 identifiers are.
/// </summary>
public static partial class StIdentifiers
{
    /// <summary>Whether <paramref name="name"/> appears as a whole identifier in <paramref name="masked"/>.</summary>
    public static bool Mentions(string masked, string name)
        => Occurrences(masked, name).Any();

    /// <summary>Every whole-identifier occurrence of <paramref name="name"/>, as offsets into <paramref name="masked"/>.</summary>
    public static IEnumerable<int> Occurrences(string masked, string name)
    {
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(
            masked, $@"(?<![A-Za-z0-9_.]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase))
        {
            yield return match.Index;
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> appears as a whole identifier, counting a namespace- or
    /// instance-qualified occurrence such as <c>MyPlc.F_Trim</c> as a mention.
    ///
    /// This is the opposite boundary rule to <see cref="Mentions"/>, and the difference matters: a
    /// local called <c>count</c> is not used by <c>fb.count</c>, but a function called
    /// <c>F_Trim</c> very much is used by <c>MyPlc.F_Trim(...)</c>, which is how one PLC project
    /// calls into another.
    /// </summary>
    public static bool MentionsQualified(string masked, string name)
    {
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > 0
            && Regex.IsMatch(
                masked,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase);
    }

    /// <summary>Whether <paramref name="name"/> is assigned with <c>:=</c> somewhere in <paramref name="masked"/>.</summary>
    public static bool IsAssigned(string masked, string name)
    {
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > 0
            && Regex.IsMatch(
                masked,
                $@"(?<![A-Za-z0-9_.]){Regex.Escape(name)}(?![A-Za-z0-9_])\s*:=",
                RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="qualifier"/>.<paramref name="member"/> is assigned with <c>:=</c>.
    /// Only qualified writes are detected; an unqualified write to a global that happens to be
    /// unambiguous is not, because deciding that needs shadowing analysis we deliberately avoid.
    /// </summary>
    public static bool AssignsMember(string masked, string qualifier, string member)
    {
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(qualifier);
        ArgumentNullException.ThrowIfNull(member);

        return qualifier.Length > 0
            && member.Length > 0
            && Regex.IsMatch(
                masked,
                $@"(?<![A-Za-z0-9_.]){Regex.Escape(qualifier)}\s*\.\s*{Regex.Escape(member)}(?![A-Za-z0-9_])\s*:=",
                RegexOptions.IgnoreCase);
    }

    /// <summary>One equality or inequality comparison found in a body.</summary>
    public readonly record struct Comparison(string Left, string Right, string Operator, int Index);

    /// <summary>
    /// Find simple equality and inequality comparisons. Only bare identifiers, dotted paths and
    /// numeric literals are recognised as operands; anything more complex is skipped rather than
    /// guessed at. <c>:=</c>, <c>&gt;=</c> and <c>&lt;=</c> are excluded by the operator pattern.
    /// </summary>
    public static IEnumerable<Comparison> Comparisons(string masked)
    {
        ArgumentNullException.ThrowIfNull(masked);

        foreach (Match match in ComparisonPattern().Matches(masked))
        {
            yield return new Comparison(
                match.Groups["lhs"].Value.Trim(),
                match.Groups["rhs"].Value.Trim(),
                match.Groups["op"].Value,
                match.Index);
        }
    }

    /// <summary>Whether a token is a floating-point literal such as <c>1.5</c> or <c>0.0</c>.</summary>
    public static bool IsRealLiteral(string token)
        => token is not null && RealLiteral().IsMatch(token);

    [GeneratedRegex(
        @"(?<lhs>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*|\d+\.\d+)"
        + @"\s*(?<op><>|(?<![:<>=])=(?!=))\s*"
        + @"(?<rhs>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*|\d+\.\d+)")]
    private static partial Regex ComparisonPattern();

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex RealLiteral();
}
