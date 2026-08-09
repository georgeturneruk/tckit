using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Renders an analysis run as SARIF 2.1.0, the OASIS interchange format for static analysis
/// results. GitHub ingests it through <c>github/codeql-action/upload-sarif</c>, which buys inline
/// annotations on the pull request diff, a Security tab that distinguishes new findings from
/// existing ones, and a dismissal UI that survives across runs. None of that is reachable from a
/// log line, however well formatted.
/// </summary>
public static class SarifWriter
{
    private const string SchemaUri =
        "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";

    /// <summary>
    /// The key under which the baseline fingerprint is published. GitHub uses partial fingerprints
    /// to recognise a finding it has seen before even when the surrounding code has moved, which is
    /// exactly what the baseline's identity is built for: it excludes the line number on purpose,
    /// so inserting a variable above a finding does not make it look new.
    /// </summary>
    public const string FingerprintKey = "tckitFingerprint/v1";

    /// <summary>
    /// Render a run. <paramref name="baseDirectory"/> is what result paths are made relative to;
    /// pass the repository root, since GitHub matches results to files by a path relative to the
    /// checkout and an absolute Windows path matches nothing.
    /// </summary>
    public static string Render(
        AnalysisResult result,
        IReadOnlyList<AnalysisFinding> findings,
        string? baseDirectory = null,
        string? toolVersion = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(findings);

        var root = new JsonObject
        {
            ["$schema"] = SchemaUri,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                Run(result, findings, baseDirectory, toolVersion),
            },
        };

        // Written through Utf8JsonWriter rather than ToJsonString(options), which routes through the
        // serializer and so wants a type resolver it has no use for here.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonObject Run(
        AnalysisResult result,
        IReadOnlyList<AnalysisFinding> findings,
        string? baseDirectory,
        string? toolVersion)
    {
        var driver = new JsonObject
        {
            ["name"] = "TcKit",
            ["informationUri"] = "https://tckit.org",
            ["rules"] = Rules(findings),
        };

        if (!string.IsNullOrEmpty(toolVersion))
        {
            // `version`, not `semanticVersion`: the latter must parse as semver, and a .NET
            // assembly version is four parts, so it would fail validation on upload.
            driver["version"] = toolVersion;
        }

        var run = new JsonObject
        {
            ["tool"] = new JsonObject { ["driver"] = driver },
            ["results"] = Results(findings, baseDirectory),
        };

        var notifications = Notifications(result);
        if (notifications.Count > 0)
        {
            // Anything that narrowed the run travels with it. A short result list from a run that
            // skipped half the project is not a clean project, and a viewer that only ever sees
            // `results` would have no way to tell the two apart.
            run["invocations"] = new JsonArray
            {
                new JsonObject
                {
                    ["executionSuccessful"] = true,
                    ["toolConfigurationNotifications"] = notifications,
                },
            };
        }

        return run;
    }

    private static JsonArray Rules(IReadOnlyList<AnalysisFinding> findings)
    {
        var rules = new JsonArray();
        foreach (var rule in RuleCatalogue.For(findings))
        {
            rules.Add(new JsonObject
            {
                ["id"] = rule.Id,
                ["name"] = Identifier(rule),
                ["shortDescription"] = new JsonObject { ["text"] = rule.Title },
                ["fullDescription"] = new JsonObject { ["text"] = rule.Description },
                ["helpUri"] = rule.HelpUri,
                ["defaultConfiguration"] = new JsonObject
                {
                    ["level"] = Level(rule.DefaultSeverity),
                },
                ["properties"] = new JsonObject
                {
                    ["category"] = rule.Category,
                    ["tags"] = new JsonArray { rule.Category },
                },
            });
        }

        return rules;
    }

    private static JsonArray Results(IReadOnlyList<AnalysisFinding> findings, string? baseDirectory)
    {
        var results = new JsonArray();
        var catalogued = RuleCatalogue.For(findings).Select(rule => rule.Id).ToList();

        foreach (var finding in findings)
        {
            var entry = new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["level"] = Level(finding.Severity),
                ["message"] = new JsonObject { ["text"] = Message(finding) },
                ["partialFingerprints"] = new JsonObject
                {
                    [FingerprintKey] = AnalysisBaseline.Fingerprint(finding),
                },
            };

            // ruleIndex is an index into the rules array; omitting it is legal, but emitting a
            // wrong one would point a viewer at somebody else's rule.
            var index = catalogued.IndexOf(finding.RuleId);
            if (index >= 0)
            {
                entry["ruleIndex"] = index;
            }

            var location = Location(finding, baseDirectory);
            if (location is not null)
            {
                entry["locations"] = new JsonArray { location };
            }

            results.Add(entry);
        }

        return results;
    }

    /// <summary>
    /// A finding with no resolvable file is emitted without a location rather than dropped or
    /// given a guessed one. A result pointing at the wrong line is worse than one pointing nowhere:
    /// uploaded, it annotates innocent code.
    /// </summary>
    private static JsonObject? Location(AnalysisFinding finding, string? baseDirectory)
    {
        if (finding.FilePath.Length == 0 || finding.FileLine <= 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["physicalLocation"] = new JsonObject
            {
                ["artifactLocation"] = new JsonObject
                {
                    ["uri"] = Uri(finding.FilePath, baseDirectory),
                },
                ["region"] = new JsonObject
                {
                    ["startLine"] = finding.FileLine,
                },
            },
            ["logicalLocations"] = new JsonArray
            {
                // The TwinCAT-native address, kept alongside the file location because that is how
                // the code is actually navigated: nobody opens a .TcPOU in a text editor.
                new JsonObject
                {
                    ["name"] = finding.ItemName.Length > 0 ? finding.ItemName : finding.ObjectName,
                    ["fullyQualifiedName"] = FullyQualifiedName(finding),
                    ["kind"] = finding.ItemName.Length > 0 ? "member" : "type",
                },
            },
        };
    }

    /// <summary>
    /// A repo-relative, forward-slash URI where the file sits under the base directory, and an
    /// absolute <c>file://</c> URI where it does not. The latter is still valid SARIF and useful
    /// locally; it just cannot be matched to a checkout, which is the honest outcome for a project
    /// that lives outside the repository being scanned.
    /// </summary>
    internal static string Uri(string filePath, string? baseDirectory)
    {
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            try
            {
                var relative = Path.GetRelativePath(baseDirectory, filePath);
                if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return relative.Replace('\\', '/');
                }
            }
            catch (ArgumentException)
            {
                // An unusable base directory falls through to the absolute form below.
            }
        }

        return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
    }

    private static JsonArray Notifications(AnalysisResult result)
    {
        var notifications = new JsonArray();

        foreach (var warning in result.ConfigWarnings)
        {
            notifications.Add(Notification("warning", $"Configuration: {warning}"));
        }

        foreach (var note in result.RulesNotRun)
        {
            notifications.Add(Notification("note", $"Rule not run: {note}"));
        }

        foreach (var skipped in result.Skipped)
        {
            notifications.Add(Notification("warning", $"Skipped: {skipped}"));
        }

        return notifications;
    }

    private static JsonObject Notification(string level, string text) => new()
    {
        ["level"] = level,
        ["message"] = new JsonObject { ["text"] = text },
    };

    private static string Message(AnalysisFinding finding)
        => finding.Suggestion.Length > 0
            ? $"{finding.Message} Suggested: '{finding.Suggestion}'."
            : finding.Message;

    private static string FullyQualifiedName(AnalysisFinding finding)
        => finding.ItemName.Length > 0
            ? $"{finding.PlcName}.{finding.ObjectName}.{finding.ItemName}"
            : $"{finding.PlcName}.{finding.ObjectName}";

    /// <summary>
    /// A PascalCase identifier for the rule. SARIF's <c>name</c> is meant to be readable in code as
    /// well as prose, and viewers show it beside the id, so it is built from the title's words
    /// rather than being another string to keep in step by hand.
    /// </summary>
    private static string Identifier(RuleDescriptor rule)
    {
        var words = rule.Title
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);

        var identifier = string.Concat(words);
        return identifier.Length > 0 ? identifier : rule.Id;
    }

    /// <summary>
    /// Our ladder onto SARIF's. <c>Suggestion</c> becomes <c>note</c>, the level GitHub renders as
    /// a low-severity alert. <c>Silent</c> and <c>None</c> never reach here: they are dropped
    /// before a finding is created.
    /// </summary>
    private static string Level(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "note",
    };
}
