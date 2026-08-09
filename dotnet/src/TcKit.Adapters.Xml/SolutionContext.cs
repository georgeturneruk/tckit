namespace TcKit.Adapters.Xml;

/// <summary>
/// The resolved target of one write verb: solution, PLC project, and its .plcproj. Built fresh per
/// verb (like the automation lane's per-verb session). PLC resolution mirrors the port contract
/// and the reader: explicit name, then the PLC_PROJECT_NAME default (only when it names a PLC in
/// this solution), then sole-PLC auto-resolution.
/// </summary>
internal sealed class SolutionContext
{
    public string SolutionPath { get; }

    public string SolutionDir { get; }

    public string PlcName { get; }

    public string PlcProjPath { get; }

    /// <summary>The PLC project root: the directory holding the .plcproj.</summary>
    public string PlcDir { get; }

    private SolutionContext(string solutionPath, string plcName, string plcProjPath)
    {
        SolutionPath = solutionPath;
        SolutionDir = Path.GetDirectoryName(solutionPath) ?? "";
        PlcName = plcName;
        PlcProjPath = plcProjPath;
        PlcDir = Path.GetDirectoryName(plcProjPath) ?? "";
    }

    public static SolutionContext Resolve(string solutionPath, string? plcName, string? envDefault)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException($"Solution path has no directory: {solutionPath}");

        var plcprojs = Directory.GetFiles(solutionDir, "*.plcproj", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in plcprojs)
        {
            byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        if (!string.IsNullOrEmpty(plcName))
        {
            if (!byName.TryGetValue(plcName, out var explicitPath))
            {
                throw new InvalidOperationException(
                    $"PLC project '{plcName}' not found in any TwinCAT project under the solution.");
            }

            return new SolutionContext(solutionPath, plcName, explicitPath);
        }

        var envName = envDefault?.Trim();
        if (!string.IsNullOrEmpty(envName) && byName.TryGetValue(envName, out var envPath))
        {
            return new SolutionContext(solutionPath, envName, envPath);
        }

        return byName.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No .plcproj found under {solutionDir}. Add a PLC project (or pass plcName explicitly)."),
            1 => new SolutionContext(solutionPath, byName.Keys.First(), byName.Values.First()),
            _ => throw new InvalidOperationException(
                $"Multiple PLC projects in solution ({string.Join(", ", byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}). "
                + "Pass plcName to disambiguate."),
        };
    }

    public PlcProjFile OpenPlcProj() => PlcProjFile.Load(PlcProjPath);

    /// <summary>
    /// Synthesise the Automation Interface tree path for a PLC-relative location, so Result
    /// details ("path", "parent_path") match the automation backend's output shape.
    /// </summary>
    public string TreePath(params string[] segments)
    {
        var suffix = segments.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var head = $"TIPC^{PlcName}^{PlcName} Project";
        return suffix.Length == 0 ? head : $"{head}^{string.Join('^', suffix)}";
    }

    /// <summary>
    /// Tree path for an on-disk location under the PLC root. Tree items carry no file extension,
    /// so a TwinCAT object file's extension is stripped from the leaf segment.
    /// </summary>
    public string TreePathOf(string fullPath)
    {
        var segments = Path.GetRelativePath(PlcDir, fullPath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0
            && Path.GetExtension(segments[^1]).StartsWith(".Tc", StringComparison.OrdinalIgnoreCase))
        {
            segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]);
        }

        return TreePath(segments);
    }

    /// <summary>The .plcproj-relative Include value (backslashes, as XAE writes them).</summary>
    public string IncludeFor(string fullPath)
        => Path.GetRelativePath(PlcDir, fullPath).Replace('/', '\\').Replace(Path.DirectorySeparatorChar, '\\');

    // --- file location ---------------------------------------------------------

    /// <summary>Find a POU file (.TcPOU or .TcIO) by its declared name, anywhere under the PLC.</summary>
    public string? FindPouFile(string name) => FindByParsedName(name, "*.TcPOU", "*.TcIO");

    public string? FindGvlFile(string name) => FindByParsedName(name, "*.TcGVL");

    public string? FindDutFile(string name) => FindByParsedName(name, "*.TcDUT");

    /// <summary>
    /// Locate any object file (POU / GVL / DUT) by name, POUs first: the shape the update verbs
    /// need, mirroring the automation lane's project-wide item search.
    /// </summary>
    public string? FindObjectFile(string name)
        => FindPouFile(name) ?? FindGvlFile(name) ?? FindDutFile(name);

    private string? FindByParsedName(string name, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(PlcDir, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                // Filename stem is the fast path; the parsed Name attribute is authoritative.
                if (!Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.Ordinal))
                {
                    continue;
                }

                return path;
            }
        }

        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(PlcDir, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (TryParsedName(path) == name)
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string? TryParsedName(string path)
    {
        try
        {
            var file = TcPlcObjectFile.Load(path);
            return file.Name;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
