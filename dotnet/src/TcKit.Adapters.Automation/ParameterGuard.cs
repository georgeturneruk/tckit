using System.Collections.Concurrent;
using TcKit.Core.Authoring;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Keeps library-parameter overrides alive across XAE saves (TASKS task 5). Parameters are spliced
/// into the .plcproj on disk while XAE holds the project open; XAE's in-memory model never learns
/// the block, so any later XAE-initiated save can silently regenerate the file without it — and the
/// compile then uses the parameter's default. Every splice registers here, and after every write
/// verb the guard re-checks the block on disk: if an XAE save dropped it, the guard re-splices it
/// (with the close/reopen cycle, so the reloaded in-memory model knows it) and errors loudly if
/// the block still does not stick.
///
/// Entries whose file or reference element vanished are dropped silently: that is a deliberate
/// delete, not a lost parameter.
///
/// The registry is process state, but the disk is the durable store: every verb seeds the
/// registry from the on-disk Parameters blocks before running (<see cref="SeedFromDisk"/>), so a
/// one-verb-per-process host (the CLI) protects blocks spliced by earlier processes exactly like
/// the long-lived server protects its own. In-process registrations take precedence over the
/// seed; they may carry values newer than what is on disk mid-verb.
/// </summary>
internal static class ParameterGuard
{
    private sealed record Entry(
        string PlcProjPath, string ElementName, string ReferenceName,
        Dictionary<string, Dictionary<string, string>> Parameters);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(
        string plcProjPath, string elementName, string referenceName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        var key = Key(plcProjPath, elementName, referenceName);
        Entries.AddOrUpdate(
            key,
            _ => new Entry(plcProjPath, elementName, referenceName, Copy(parameters)),
            (_, existing) =>
            {
                foreach (var (listName, keys) in parameters)
                {
                    if (!existing.Parameters.TryGetValue(listName, out var mergedKeys))
                    {
                        mergedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        existing.Parameters[listName] = mergedKeys;
                    }

                    foreach (var (k, v) in keys)
                    {
                        mergedKeys[k] = v;
                    }
                }

                return existing;
            });
    }

    /// <summary>Forget a reference's parameters (its deliberate deletion must not trigger a restore).</summary>
    public static void Unregister(string plcProjPath, string referenceName)
    {
        foreach (var key in Entries.Keys.Where(k =>
            Entries.TryGetValue(k, out var e)
            && string.Equals(e.PlcProjPath, plcProjPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ReferenceName, referenceName, StringComparison.OrdinalIgnoreCase)))
        {
            Entries.TryRemove(key, out _);
        }
    }

    /// <summary>Drop everything (test isolation).</summary>
    public static void Clear() => Entries.Clear();

    /// <summary>
    /// Adopt every Parameters block already on disk under the solution as a guarded entry
    /// (existing registrations win). Run before each verb: whatever the previous verb's
    /// VerifyOrRestore left on disk is the truth a fresh process must keep defending.
    /// </summary>
    public static void SeedFromDisk(string? solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
        {
            return;
        }

        foreach (var plcProjPath in Directory.EnumerateFiles(solutionDir, "*.plcproj", SearchOption.AllDirectories))
        {
            foreach (var block in PlcProjXml.ReadReferenceParameters(plcProjPath))
            {
                Entries.TryAdd(
                    Key(plcProjPath, block.ElementName, block.ReferenceName),
                    new Entry(plcProjPath, block.ElementName, block.ReferenceName, Copy(block.Parameters)));
            }
        }
    }

    /// <summary>
    /// Re-check every registered block on disk and restore any an XAE save dropped. Returns the
    /// restored reference names (empty when everything held). Throws when a restore does not stick.
    /// </summary>
    public static IReadOnlyList<string> VerifyOrRestore(ITcSession session)
    {
        if (Entries.IsEmpty)
        {
            return [];
        }

        var missing = new List<Entry>();
        foreach (var entry in Entries.Values)
        {
            if (!File.Exists(entry.PlcProjPath)
                || !PlcProjXml.HasReference(entry.PlcProjPath, entry.ElementName, entry.ReferenceName))
            {
                // The project or the reference itself is gone: a deliberate delete, not a lost block.
                Entries.TryRemove(Key(entry.PlcProjPath, entry.ElementName, entry.ReferenceName), out _);
                continue;
            }

            if (!PlcProjXml.HasParameters(entry.PlcProjPath, entry.ElementName, entry.ReferenceName, Freeze(entry)))
            {
                missing.Add(entry);
            }
        }

        if (missing.Count == 0)
        {
            return [];
        }

        // One close/reopen cycle restores all dropped blocks: splice while the solution is closed,
        // then reopen so the rehydrated in-memory model includes them.
        var solutionPath = session.SolutionPath;
        session.CloseSolution();
        foreach (var entry in missing)
        {
            PlcProjXml.SetReferenceParameters(
                entry.PlcProjPath, entry.ElementName, entry.ReferenceName, Freeze(entry));
        }

        session.UseSolution(solutionPath);

        foreach (var entry in missing)
        {
            if (!PlcProjXml.HasParameters(entry.PlcProjPath, entry.ElementName, entry.ReferenceName, Freeze(entry)))
            {
                throw new InvalidOperationException(
                    $"Library parameters on '{entry.ReferenceName}' ({entry.PlcProjPath}) were lost by an XAE "
                    + "save and could not be restored. The override will not reach the compile; set the "
                    + "parameters in the XAE Library Manager UI or re-open the solution and retry.");
            }
        }

        return missing.Select(e => e.ReferenceName).ToList();
    }

    private static string Key(string path, string element, string name)
        => $"{path}|{element}|{name}".ToUpperInvariant();

    private static Dictionary<string, Dictionary<string, string>> Copy(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
        => parameters.ToDictionary(
            list => list.Key,
            list => list.Value.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Freeze(Entry entry)
        => entry.Parameters.ToDictionary(
            list => list.Key,
            list => (IReadOnlyDictionary<string, string>)list.Value,
            StringComparer.OrdinalIgnoreCase);
}
