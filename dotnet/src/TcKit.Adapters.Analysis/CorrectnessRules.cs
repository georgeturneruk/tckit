using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Rules for code that compiles and is still wrong. Every rule here exists because
/// <c>Build</c> would not have caught it; anything the compiler reports is the compiler's job.
///
/// Each rule carries a guard that keeps it precise, because a false positive is worse than a
/// miss: an agent will "fix" a bogus finding and break working code.
/// </summary>
public static class CorrectnessRules
{
    public const string CorrectnessCategory = "correctness";

    public const string StructureCategory = "structure";

    /// <summary>A function block instance on a call stack, so its state resets every call.</summary>
    public const string StatelessInstanceId = "TCK2001";

    /// <summary>REAL or LREAL compared for exact equality.</summary>
    public const string RealEqualityId = "TCK2002";

    /// <summary>RETAIN or PERSISTENT where it cannot survive a restart.</summary>
    public const string MisplacedRetainId = "TCK2003";

    /// <summary>A local declared and never referenced.</summary>
    public const string UnusedLocalId = "TCK2004";

    /// <summary>A function block input that nothing ever reads.</summary>
    public const string UnreadInputId = "TCK2005";

    /// <summary>A global written from more than one POU.</summary>
    public const string MultiWriterGlobalId = "TCK3001";

    /// <summary>A POU that nothing instantiates, calls, or binds to a task.</summary>
    public const string UnreachableObjectId = "TCK3002";

    /// <summary>Rules that need the whole project in hand, so a scoped run must skip them.</summary>
    public static IReadOnlyList<string> WholeProjectRules { get; } =
        [UnreadInputId, MultiWriterGlobalId, UnreachableObjectId];

    /// <summary>Run every correctness and structure rule over the parsed project.</summary>
    public static List<AnalysisFinding> Run(AnalysedProject project, AnalysisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        var findings = new List<AnalysisFinding>();

        foreach (var pou in project.Pous)
        {
            StatelessInstances(pou, project.Classifier, settings, findings);
            RealEquality(pou, project.Classifier, settings, findings);
            MisplacedRetain(pou, settings, findings);
            UnusedLocals(pou, settings, findings);
        }

        if (!project.IsWholeProject)
        {
            return findings;
        }

        UnreadInputs(project, settings, findings);
        MultiWriterGlobals(project, settings, findings);
        UnreachableObjects(project, settings, findings);

        return findings;
    }

    /// <summary>
    /// TCK2001: a function block instance declared on a call stack. A method's <c>VAR</c> is stack
    /// storage, so the instance is constructed fresh on every call and any edge detection, timer or
    /// internal state silently resets. Compiles perfectly; the classic TwinCAT trap.
    /// </summary>
    private static void StatelessInstances(
        AnalysedPou pou, TypeClassifier classifier, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        foreach (var member in pou.Members)
        {
            foreach (var variable in member.Declaration.Variables)
            {
                // VAR_INST is the correct construct for per-instance state in a method, and
                // VAR_TEMP states the intent explicitly, so neither is a mistake.
                if (variable.Section is not VarSection.Var
                    || classifier.Classify(variable.TypeExpression) is not TypeClass.FbInstance)
                {
                    continue;
                }

                Add(findings, settings, StatelessInstanceId, CorrectnessCategory, DiagnosticSeverity.Warning,
                    $"Function block instance '{variable.Name}' is declared in the VAR block of "
                    + $"'{member.Source.Name}', so it is reconstructed on every call and loses its state. "
                    + "Declare it in the function block's own VAR block, or use VAR_INST.",
                    pou, member.Source.Name, CodePart.Declaration, variable.Line, variable.Name);
            }
        }

        // A FUNCTION has no instance either, so its own VAR is stack storage in the same way.
        if (pou.Source.PouType is not PouType.Function)
        {
            return;
        }

        foreach (var variable in pou.Declaration.Variables)
        {
            if (variable.Section is VarSection.Var
                && classifier.Classify(variable.TypeExpression) is TypeClass.FbInstance)
            {
                Add(findings, settings, StatelessInstanceId, CorrectnessCategory, DiagnosticSeverity.Warning,
                    $"Function block instance '{variable.Name}' is declared in a FUNCTION, which has no "
                    + "instance storage, so its state is reset on every call. Use a FUNCTION_BLOCK instead.",
                    pou, "", CodePart.Declaration, variable.Line, variable.Name);
            }
        }
    }

    /// <summary>
    /// TCK2002: exact equality on a floating-point value. Compiles and usually appears to work, then
    /// fails on a value that is not exactly representable.
    /// </summary>
    private static void RealEquality(
        AnalysedPou pou, TypeClassifier classifier, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        var objectReals = RealNames(pou.Declaration, classifier);

        Scan(pou.MaskedBody, objectReals, "");
        foreach (var member in pou.Members)
        {
            var scope = new HashSet<string>(objectReals, StringComparer.OrdinalIgnoreCase);
            scope.UnionWith(RealNames(member.Declaration, classifier));
            Scan(member.MaskedBody, scope, member.Source.Name);
        }

        void Scan(string body, HashSet<string> reals, string itemName)
        {
            foreach (var comparison in StIdentifiers.Comparisons(body))
            {
                // A float literal on either side is decisive on its own; otherwise one operand has
                // to be a declared REAL. Dotted paths are left alone rather than guessed at.
                var involvesReal = reals.Contains(comparison.Left)
                    || reals.Contains(comparison.Right)
                    || StIdentifiers.IsRealLiteral(comparison.Left)
                    || StIdentifiers.IsRealLiteral(comparison.Right);

                if (!involvesReal)
                {
                    continue;
                }

                Add(findings, settings, RealEqualityId, CorrectnessCategory, DiagnosticSeverity.Warning,
                    $"'{comparison.Left} {comparison.Operator} {comparison.Right}' compares a "
                    + "floating-point value for exact equality. Compare against a tolerance instead.",
                    pou, itemName, CodePart.Implementation,
                    StSource.LineAt(body, comparison.Index), comparison.Left);
            }
        }
    }

    /// <summary>
    /// TCK2003: RETAIN or PERSISTENT on a member-scope declaration. Retention applies to storage
    /// that outlives a call; on a local it is silently meaningless.
    /// </summary>
    private static void MisplacedRetain(
        AnalysedPou pou, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        foreach (var member in pou.Members)
        {
            foreach (var variable in member.Declaration.Variables)
            {
                if (variable.Qualifiers.HasFlag(VarQualifiers.Retain)
                    || variable.Qualifiers.HasFlag(VarQualifiers.Persistent))
                {
                    Add(findings, settings, MisplacedRetainId, CorrectnessCategory, DiagnosticSeverity.Warning,
                        $"'{variable.Name}' is declared RETAIN or PERSISTENT inside "
                        + $"'{member.Source.Name}', where it cannot survive a restart. Move it to the "
                        + "function block's VAR block or a VAR_GLOBAL RETAIN list.",
                        pou, member.Source.Name, CodePart.Declaration, variable.Line, variable.Name);
                }
            }
        }
    }

    /// <summary>
    /// TCK2004: a local nothing references. Restricted to member scope (and to a FUNCTION's own
    /// locals) because a function block's VAR members are reachable from outside in TwinCAT 3, so
    /// an apparently unused one may be part of its API.
    /// </summary>
    private static void UnusedLocals(
        AnalysedPou pou, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        foreach (var member in pou.Members)
        {
            if (!HasImplementation(member.MaskedBody))
            {
                continue;
            }

            foreach (var variable in member.Declaration.Variables)
            {
                if (variable.Section is VarSection.Var or VarSection.VarTemp
                    && !StIdentifiers.Mentions(member.MaskedBody, variable.Name))
                {
                    Add(findings, settings, UnusedLocalId, CorrectnessCategory, DiagnosticSeverity.Warning,
                        $"Local '{variable.Name}' is declared in '{member.Source.Name}' and never used.",
                        pou, member.Source.Name, CodePart.Declaration, variable.Line, variable.Name);
                }
            }
        }

        if (pou.Source.PouType is not PouType.Function || !HasImplementation(pou.MaskedBody))
        {
            return;
        }

        foreach (var variable in pou.Declaration.Variables)
        {
            if (variable.Section is VarSection.Var or VarSection.VarTemp
                && !StIdentifiers.Mentions(pou.MaskedBody, variable.Name))
            {
                Add(findings, settings, UnusedLocalId, CorrectnessCategory, DiagnosticSeverity.Warning,
                    $"Local '{variable.Name}' is declared in '{pou.Name}' and never used.",
                    pou, "", CodePart.Declaration, variable.Line, variable.Name);
            }
        }
    }

    /// <summary>
    /// Whether a body contains any code at all. An empty body or a bare <c>;</c> is a stub, and the
    /// unused-declaration rules have nothing useful to say about one.
    /// </summary>
    private static bool HasImplementation(string maskedBody)
        => maskedBody.Any(char.IsLetterOrDigit);

    /// <summary>
    /// TCK2005: a function block input nothing reads, which usually means a wiring mistake rather
    /// than a spare parameter. Guarded by inheritance: if anything extends the function block, a
    /// child may be the reader, so the input is left alone.
    /// </summary>
    private static void UnreadInputs(
        AnalysedProject project, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        var extended = new HashSet<string>(
            project.Pous
                .Select(pou => pou.Declaration.Header.Extends)
                .Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pou in project.Pous)
        {
            // An unimplemented stub reads none of its inputs by definition. Reporting each one
            // tells the author nothing they do not already know, and a TDD flow starts here.
            if (pou.Source.PouType is not PouType.FunctionBlock
                || pou.Declaration.Header.IsAbstract
                || extended.Contains(pou.Name)
                || !HasImplementation(pou.AllBodies))
            {
                continue;
            }

            foreach (var variable in pou.Declaration.Variables)
            {
                if (variable.Section is VarSection.VarInput
                    && !StIdentifiers.Mentions(pou.AllBodies, variable.Name))
                {
                    Add(findings, settings, UnreadInputId, CorrectnessCategory, DiagnosticSeverity.Warning,
                        $"Input '{variable.Name}' of '{pou.Name}' is never read anywhere in the "
                        + "function block.",
                        pou, "", CodePart.Declaration, variable.Line, variable.Name);
                }
            }
        }
    }

    /// <summary>
    /// TCK3001: a global written from more than one POU. On separate tasks that is a race; on the
    /// same task it is still a maintenance hazard, because the last writer in scan order silently
    /// wins. Only qualified writes are detected, so this under-reports rather than guesses.
    /// </summary>
    private static void MultiWriterGlobals(
        AnalysedProject project, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        foreach (var gvl in project.Gvls)
        {
            foreach (var variable in gvl.Declaration.Variables)
            {
                var writers = project.Pous
                    .Where(pou => StIdentifiers.AssignsMember(pou.AllBodies, gvl.Source.Name, variable.Name))
                    .Select(pou => pou.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (writers.Count > 1)
                {
                    Add(findings, settings, MultiWriterGlobalId, StructureCategory, DiagnosticSeverity.Suggestion,
                        $"Global '{gvl.Source.Name}.{variable.Name}' is written from "
                        + $"{writers.Count} POUs ({string.Join(", ", writers)}). Give it a single owner.",
                        gvl.PlcName, gvl.Source.Name, "", variable.Line, variable.Name);
                }
            }
        }
    }

    /// <summary>
    /// TCK3002: a POU nothing instantiates, calls, or binds to a task. The search spans every PLC
    /// project in the solution, so a library consumed only by a sibling test project is not
    /// reported. A POU in a library meant for external consumers will still show up, which is why
    /// this ships as a suggestion.
    /// </summary>
    private static void UnreachableObjects(
        AnalysedProject project, AnalysisSettings settings, List<AnalysisFinding> findings)
    {
        var taskPrograms = new HashSet<string>(
            project.Structure.Tasks.SelectMany(task => task.Programs), StringComparer.OrdinalIgnoreCase);

        foreach (var pou in project.Pous)
        {
            if (NamingRuleEngine.IsReserved(pou.Name) || taskPrograms.Contains(pou.Name))
            {
                continue;
            }

            if (IsReferencedElsewhere(project, pou))
            {
                continue;
            }

            Add(findings, settings, UnreachableObjectId, StructureCategory, DiagnosticSeverity.Suggestion,
                $"'{pou.Name}' is never instantiated, called, or bound to a task anywhere in the "
                + "solution. It may be dead code, or intended for consumers outside this solution.",
                pou, "", CodePart.Declaration, 1, pou.Name);
        }
    }

    private static bool IsReferencedElsewhere(AnalysedProject project, AnalysedPou target)
    {
        foreach (var other in project.Pous)
        {
            if (ReferenceEquals(other, target))
            {
                continue;
            }

            var header = other.Declaration.Header;
            if (header.Extends.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                || header.Implements.Any(name => name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Qualified matching throughout: one PLC project calls into another as
            // "OtherPlc.F_Trim", and treating that as a non-reference would report every library
            // POU consumed by a sibling test project as dead.
            if (DeclaresType(other.Declaration, target.Name)
                || other.Members.Any(member => DeclaresType(member.Declaration, target.Name))
                || StIdentifiers.MentionsQualified(other.AllBodies, target.Name))
            {
                return true;
            }
        }

        return project.Gvls.Any(gvl => DeclaresType(gvl.Declaration, target.Name))
            || project.Duts.Any(dut => dut.Declaration.Members.Any(
                member => MentionsType(member.TypeExpression, target.Name)));
    }

    private static bool DeclaresType(StDeclaration declaration, string typeName)
        => declaration.Variables.Any(variable => MentionsType(variable.TypeExpression, typeName));

    private static bool MentionsType(string typeExpression, string typeName)
        => StIdentifiers.MentionsQualified(typeExpression, typeName);

    private static HashSet<string> RealNames(StDeclaration declaration, TypeClassifier classifier)
        => new(
            declaration.Variables
                .Where(variable => classifier.Classify(variable.TypeExpression) is TypeClass.Real)
                .Select(variable => variable.Name),
            StringComparer.OrdinalIgnoreCase);

    private static void Add(
        List<AnalysisFinding> findings,
        AnalysisSettings settings,
        string ruleId,
        string category,
        DiagnosticSeverity fallback,
        string message,
        AnalysedPou pou,
        string itemName,
        CodePart part,
        int line,
        string symbol)
        => Add(findings, settings, ruleId, category, fallback, message,
            pou.PlcName, pou.Name, itemName, line, symbol, part);

    private static void Add(
        List<AnalysisFinding> findings,
        AnalysisSettings settings,
        string ruleId,
        string category,
        DiagnosticSeverity fallback,
        string message,
        string plcName,
        string objectName,
        string itemName,
        int line,
        string symbol,
        CodePart part = CodePart.Declaration)
    {
        var severity = settings.SeverityFor(ruleId, category, fallback);
        if (severity is DiagnosticSeverity.None)
        {
            return;
        }

        findings.Add(new AnalysisFinding
        {
            RuleId = ruleId,
            Category = category,
            Severity = severity,
            Message = message,
            PlcName = plcName,
            ObjectName = objectName,
            ItemName = itemName,
            Part = part,
            Line = line,
            Symbol = symbol,
        });
    }
}
