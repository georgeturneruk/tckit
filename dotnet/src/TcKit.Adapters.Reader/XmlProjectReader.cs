using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Reader;

/// <summary>
/// File-based <see cref="IProjectReader"/>: builds a <see cref="ProjectStructure"/> by walking
/// the on-disk project tree and parsing the TwinCAT XML files. No COM, no ADS, no running XAE.
/// Ported from the Python <c>tckit/adapters/readers/xml_reader.py</c>.
///
/// Stateful, like the Python reader: <see cref="GetStructureAsync"/> builds a symbol index
/// (per-PLC name -> file path) that the per-symbol reads resolve against. Call get_structure first
/// in a session. A <see cref="DateTime"/> staleness check rebuilds the index when a .plcproj
/// changes on disk (ADR-0005). Registered as a singleton, so a lock guards the shared index.
/// </summary>
public sealed class XmlProjectReader : IProjectReader
{
    private readonly object _sync = new();
    private Dictionary<string, Dictionary<string, string>> _fileIndex = new(StringComparer.Ordinal);
    private Dictionary<string, string> _plcprojByName = new(StringComparer.Ordinal);
    private Dictionary<string, DateTime> _plcprojMtimes = new(StringComparer.Ordinal);
    private string? _indexProjectPath;

    public Task<ProjectStructure> GetStructureAsync(
        string projectPath, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(BuildStructure(projectPath, plcName));
        }
    }

    public Task<PouInterface> GetPouInterfaceAsync(
        string pouName, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var pou = TcFileParser.ParsePouFull(Resolve(pouName, ".TcPOU", plcName));
            var methods = pou.Methods.Select(m => new MethodSignature
            {
                Name = m.Name,
                ReturnType = TcFileParser.ExtractMethodReturnType(m.Declaration),
                // Method locals are implementation detail, not API surface; get_pou_item keeps them.
                Declaration = TcFileParser.StripMethodLocals(m.Declaration),
            }).ToList();
            var properties = pou.Properties.Select(p => new PropertySignature
            {
                Name = p.Name,
                ReturnType = TcFileParser.ExtractPropertyReturnType(p.Declaration),
                Declaration = p.Declaration,
                HasGet = p.Get is not null,
                HasSet = p.Set is not null,
            }).ToList();

            return Task.FromResult(new PouInterface
            {
                PouName = pouName,
                PouType = pou.Type,
                Declaration = pou.Declaration,
                Methods = methods,
                Properties = properties,
                Actions = pou.Actions.Select(a => a.Name).ToList(),
            });
        }
    }

    public Task<PouDeclaration> GetPouDeclarationAsync(
        string pouName, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var pou = TcFileParser.ParsePouFull(Resolve(pouName, ".TcPOU", plcName));
            return Task.FromResult(new PouDeclaration
            {
                PouName = pouName,
                PouType = pou.Type,
                Declaration = pou.Declaration,
            });
        }
    }

    public Task<PouItem> GetPouItemAsync(
        string pouName, string itemName, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var path = Resolve(pouName, ".TcPOU", plcName);
            var pou = TcFileParser.ParsePouFull(path);

            // Property accessor syntax first ("PropName.Get" / "PropName.Set").
            var dot = itemName.LastIndexOf('.');
            if (dot >= 0)
            {
                var propName = itemName[..dot];
                var accessor = itemName[(dot + 1)..].ToLowerInvariant();
                var prop = pou.Properties.FirstOrDefault(p => p.Name == propName)
                    ?? throw new FileNotFoundException(
                        $"Property '{propName}' not found in POU '{pouName}' ({path})");
                var part = accessor switch
                {
                    "get" => prop.Get,
                    "set" => prop.Set,
                    _ => null,
                };
                if (part is null)
                {
                    var capitalised = char.ToUpperInvariant(accessor[0]) + accessor[1..];
                    throw new FileNotFoundException(
                        $"Property '{propName}' in '{pouName}' has no {capitalised} accessor");
                }

                return Task.FromResult(new PouItem
                {
                    PouName = pouName,
                    ItemName = itemName,
                    Declaration = part.Declaration,
                    Body = part.Body,
                });
            }

            // Methods, then actions, then a bare property header (body lives in .Get / .Set).
            var member = pou.Methods.FirstOrDefault(m => m.Name == itemName)
                ?? pou.Actions.FirstOrDefault(a => a.Name == itemName);
            if (member is not null)
            {
                return Task.FromResult(new PouItem
                {
                    PouName = pouName,
                    ItemName = itemName,
                    Declaration = member.Declaration,
                    Body = member.Body,
                });
            }

            var bareProperty = pou.Properties.FirstOrDefault(p => p.Name == itemName);
            if (bareProperty is not null)
            {
                return Task.FromResult(new PouItem
                {
                    PouName = pouName,
                    ItemName = itemName,
                    Declaration = bareProperty.Declaration,
                    Body = "",
                });
            }

            throw new FileNotFoundException($"Item '{itemName}' not found in POU '{pouName}' ({path})");
        }
    }

    public Task<Gvl> GetGvlAsync(string gvlName, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var path = Resolve(gvlName, ".TcGVL", plcName);
            var gvl = TcFileParser.ParseGvlFull(path);
            return Task.FromResult(new Gvl { Name = gvlName, Path = path, Declaration = gvl.Declaration });
        }
    }

    public Task<Dut> GetDutAsync(string dutName, string? plcName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var path = Resolve(dutName, ".TcDUT", plcName);
            var dut = TcFileParser.ParseDutFull(path);
            return Task.FromResult(new Dut
            {
                Name = dutName,
                Path = path,
                Declaration = dut.Declaration,
                DutKind = dut.Kind,
                BaseType = dut.BaseType,
            });
        }
    }

    private ProjectStructure BuildStructure(string projectPath, string? plcName)
    {
        var root = projectPath;
        var isFile = File.Exists(root);
        if (!isFile && !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Project path not found: {projectPath}");
        }

        // Accept a .sln file path as shorthand for its containing directory.
        if (isFile && string.Equals(Path.GetExtension(root), ".sln", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var slnPaths = EnumerateSorted(root, "*.sln", ".sln");
        var solutionPath = slnPaths.Count > 0 ? Path.GetFullPath(slnPaths[0]) : "";

        var plcprojPaths = EnumerateSorted(root, "*.plcproj", ".plcproj");

        // Reset the index even on a scoped walk (mirrors the Python reader).
        var fileIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var plcprojByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var plcprojMtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var plcproj in plcprojPaths)
        {
            var stem = Path.GetFileNameWithoutExtension(plcproj);
            if (plcprojByName.TryAdd(stem, plcproj))
            {
                plcprojMtimes[stem] = LastWriteOrMin(plcproj);
            }
        }

        var plcs = new Dictionary<string, PlcSection>(StringComparer.Ordinal);
        if (plcprojByName.Count > 0)
        {
            foreach (var (name, plcproj) in plcprojByName)
            {
                if (plcName is not null && name != plcName)
                {
                    continue;
                }

                var index = new Dictionary<string, string>(StringComparer.Ordinal);
                plcs[name] = BuildSection(name, Path.GetDirectoryName(plcproj)!, plcproj, [plcproj], index);
                fileIndex[name] = index;
            }

            if (plcName is not null && !plcs.ContainsKey(plcName))
            {
                var available = string.Join(", ", plcprojByName.Keys.OrderBy(n => n, StringComparer.Ordinal));
                throw new ArgumentException(
                    $"plc_name '{plcName}' does not match any PLC project. Available: {available}.");
            }
        }
        else
        {
            // No .plcproj anywhere: synthesise one PLC named after the directory basename.
            var anonName = plcName ?? new DirectoryInfo(root).Name;
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            plcs[anonName] = BuildSection(anonName, root, "", librariesFrom: null, index);
            fileIndex[anonName] = index;
        }

        _fileIndex = fileIndex;
        _plcprojByName = plcprojByName;
        _plcprojMtimes = plcprojMtimes;
        _indexProjectPath = projectPath;

        return new ProjectStructure
        {
            ProjectPath = projectPath,
            SolutionPath = solutionPath,
            Plcs = plcs,
            Tasks = CollectTasks(root),
        };
    }

    private static PlcSection BuildSection(
        string plcName, string folderRoot, string plcprojPath,
        IReadOnlyList<string>? librariesFrom, Dictionary<string, string> index)
    {
        var pous = new List<PouRef>();
        foreach (var file in EnumerateSorted(folderRoot, "*.TcPOU", ".TcPOU"))
        {
            var meta = TryParse(() => TcFileParser.ParsePou(file));
            if (meta is null)
            {
                continue;
            }

            index[meta.Name] = file;
            pous.Add(new PouRef
            {
                Name = meta.Name,
                PouType = meta.Type,
                Path = file,
                PlcName = plcName,
                Folder = FolderFor(file, folderRoot),
            });
        }

        var gvls = new List<GvlRef>();
        foreach (var file in EnumerateSorted(folderRoot, "*.TcGVL", ".TcGVL"))
        {
            var name = TryParse(() => TcFileParser.ParseGvl(file));
            if (name is null)
            {
                continue;
            }

            index[name] = file;
            gvls.Add(new GvlRef
            {
                Name = name,
                Path = file,
                PlcName = plcName,
                Folder = FolderFor(file, folderRoot),
            });
        }

        var duts = new List<DutRef>();
        foreach (var file in EnumerateSorted(folderRoot, "*.TcDUT", ".TcDUT"))
        {
            var meta = TryParse(() => TcFileParser.ParseDut(file));
            if (meta is null)
            {
                continue;
            }

            index[meta.Name] = file;
            duts.Add(new DutRef
            {
                Name = meta.Name,
                Path = file,
                PlcName = plcName,
                DutKind = meta.Kind,
                Folder = FolderFor(file, folderRoot),
            });
        }

        var libraries = librariesFrom is not null ? CollectLibraries(librariesFrom) : [];

        return new PlcSection
        {
            Name = plcName,
            PlcprojPath = plcprojPath,
            Pous = pous,
            Gvls = gvls,
            Duts = duts,
            Libraries = libraries,
        };
    }

    /// <summary>
    /// Resolve a symbol name to its file path against the index (ADR-0005): an explicit
    /// <paramref name="plcName"/> wins, then the PLC_PROJECT_NAME env default, then a unique-symbol
    /// fallback; ambiguity across PLC projects is an error. The index must be populated first.
    /// </summary>
    private string Resolve(string name, string extension, string? plcName)
    {
        RefreshIfStale();

        if (_fileIndex.Count == 0)
        {
            throw new FileNotFoundException(
                $"No {extension} file found for '{name}'. Call get_structure with a project path first.");
        }

        if (plcName is not null)
        {
            if (!_fileIndex.TryGetValue(plcName, out var scoped))
            {
                var available = string.Join(", ", _fileIndex.Keys.OrderBy(k => k, StringComparer.Ordinal));
                throw new ArgumentException(
                    $"plc_name '{plcName}' does not match any PLC project. Available: {available}.");
            }

            if (scoped.TryGetValue(name, out var scopedPath))
            {
                return scopedPath;
            }

            throw new FileNotFoundException(
                $"No {extension} file found for '{name}' in PLC project '{plcName}'.");
        }

        var envDefault = Environment.GetEnvironmentVariable("PLC_PROJECT_NAME")?.Trim();
        if (!string.IsNullOrEmpty(envDefault) && _fileIndex.TryGetValue(envDefault, out var envSection))
        {
            if (envSection.TryGetValue(name, out var envPath))
            {
                return envPath;
            }

            throw new FileNotFoundException(
                $"No {extension} file found for '{name}' in PLC project '{envDefault}' "
                + "(PLC_PROJECT_NAME env default).");
        }

        var owning = _fileIndex.Where(kv => kv.Value.ContainsKey(name)).Select(kv => kv.Key).ToList();
        if (owning.Count == 1)
        {
            return _fileIndex[owning[0]][name];
        }

        if (owning.Count > 1)
        {
            var names = string.Join(", ", owning.OrderBy(k => k, StringComparer.Ordinal));
            throw new ArgumentException(
                $"Symbol '{name}' exists in multiple PLC projects ({names}). Pass plc_name to disambiguate.");
        }

        var indexed = string.Join(", ", _fileIndex.Keys.OrderBy(k => k, StringComparer.Ordinal));
        throw new FileNotFoundException(
            $"No {extension} file found for '{name}' in any indexed PLC project. Indexed: {indexed}.");
    }

    /// <summary>Rebuild the index if any tracked .plcproj has changed on disk (body edits don't touch it).</summary>
    private void RefreshIfStale()
    {
        if (_plcprojByName.Count == 0 || _indexProjectPath is null)
        {
            return;
        }

        foreach (var (name, plcproj) in _plcprojByName)
        {
            if (LastWriteOrMin(plcproj) != _plcprojMtimes.GetValueOrDefault(name))
            {
                BuildStructure(_indexProjectPath, null);
                return;
            }
        }
    }

    private static DateTime LastWriteOrMin(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static IReadOnlyList<LibraryRef> CollectLibraries(IReadOnlyList<string> plcprojPaths)
    {
        var seen = new HashSet<(string, string?)>();
        var result = new List<LibraryRef>();
        foreach (var plcproj in plcprojPaths)
        {
            var data = TryParse(() => TcFileParser.ParsePlcproj(plcproj));
            if (data is null)
            {
                continue;
            }

            foreach (var lib in data)
            {
                if (!seen.Add((lib.Name, lib.Placeholder)))
                {
                    continue;
                }

                result.Add(new LibraryRef { Name = lib.Name, Version = lib.Version, Placeholder = lib.Placeholder });
            }
        }

        return result;
    }

    private static IReadOnlyList<TaskInfo> CollectTasks(string root)
    {
        // Prefer .TcTTO (cycle in µs + bound POU), then merge any .tsproj tasks lacking a
        // .TcTTO counterpart. First writer wins per task name.
        var byName = new Dictionary<string, TaskInfo>(StringComparer.Ordinal);
        var order = new List<string>();

        void Add(TcFileParser.TaskRaw raw)
        {
            if (string.IsNullOrEmpty(raw.Name) || byName.ContainsKey(raw.Name))
            {
                return;
            }

            byName[raw.Name] = new TaskInfo
            {
                Name = raw.Name,
                CycleTimeUs = raw.CycleTimeUs,
                Priority = raw.Priority,
                Programs = raw.Programs.ToList(),
            };
            order.Add(raw.Name);
        }

        foreach (var file in EnumerateSorted(root, "*.TcTTO", ".TcTTO"))
        {
            var raw = TryParse(() => TcFileParser.ParseTctto(file));
            if (raw is not null)
            {
                Add(raw);
            }
        }

        foreach (var file in EnumerateSorted(root, "*.tsproj", ".tsproj"))
        {
            var list = TryParse(() => TcFileParser.ParseTsproj(file));
            if (list is null)
            {
                continue;
            }

            foreach (var raw in list)
            {
                Add(raw);
            }
        }

        return order.Select(name => byName[name]).ToList();
    }

    /// <summary>
    /// Enumerate files recursively, sorted to match the Python <c>sorted(rglob())</c> order
    /// (case-insensitive, component-wise). The extension filter guards against the legacy
    /// Windows wildcard quirk where "*.sln" can also match e.g. ".slnx".
    /// </summary>
    private static List<string> EnumerateSorted(string root, string pattern, string extension)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var files = Directory
            .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
        files.Sort(PathComponentComparer.Instance);
        return files;
    }

    private static string FolderFor(string filePath, string folderRoot)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        var rootFull = Path.GetFullPath(folderRoot);
        var rel = Path.GetRelativePath(rootFull, dir);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return "";
        }

        return rel.Replace('\\', '/');
    }

    private static T? TryParse<T>(Func<T> parse)
        where T : class
    {
        try
        {
            return parse();
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares paths component-wise to mirror Python's <c>sorted(rglob())</c> on Windows, which
    /// case-folds by lower-casing (os.path.normcase). This matters because '_' (0x5F) sits between
    /// the upper- and lower-case alphabets: lower-casing puts 'F_x' before 'FB_x', whereas an
    /// upper-casing OrdinalIgnoreCase compare would flip them.
    /// </summary>
    private sealed class PathComponentComparer : IComparer<string>
    {
        public static PathComponentComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var xs = Split(x);
            var ys = Split(y);
            var shared = Math.Min(xs.Length, ys.Length);
            for (var i = 0; i < shared; i++)
            {
                var cmp = string.CompareOrdinal(xs[i].ToLowerInvariant(), ys[i].ToLowerInvariant());
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return xs.Length.CompareTo(ys.Length);
        }

        private static string[] Split(string? path)
            => (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }
}
