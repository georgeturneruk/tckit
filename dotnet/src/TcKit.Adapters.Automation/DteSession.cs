using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Tree-navigation and source-write helpers over a late-bound TcXaeShell DTE / ITcSysManager.
/// Faithful port of the bridge harness (_TcDte.psm1). Every method here assumes it is running on the
/// STA worker thread (see <see cref="StaExecutor"/>); callers must not touch COM off that thread.
/// Late binding (dynamic) is deliberate: it needs no interop assembly, so the solution still builds
/// on a machine without TwinCAT, and it matches the proven Phase-0 spike.
/// </summary>
internal static partial class DteSession
{
    private static string ProgId
        => $"TcXaeShell.DTE.{Environment.GetEnvironmentVariable("COM_VERSION") ?? "17.0"}";

    /// <summary>Attach to the running TcXaeShell instance, or throw an actionable error.</summary>
    public static dynamic Attach()
    {
        try
        {
            return NativeMethods.GetActiveObject(ProgId);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"No running {ProgId} found. Open your TwinCAT solution in TcXaeShell first.", ex);
        }
    }

    /// <summary>Operate on the solution at <paramref name="path"/> (opening it), or the open one when empty.</summary>
    public static void UseSolution(dynamic dte, string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            OpenSolution(dte, path);
            return;
        }

        string current = dte.Solution.FullName;
        if (string.IsNullOrEmpty(current))
        {
            throw new InvalidOperationException(
                "No solution is open in TcXaeShell. Call OpenProject (or open it in XAE) first.");
        }

        WaitPlcProjectsLoaded(dte);
    }

    public static void OpenSolution(dynamic dte, string path)
    {
        ComRetry.Invoke(() => dte.Solution.Open(path));
        WaitPlcProjectsLoaded(dte);
    }

    /// <summary>Force-materialise each PLC project's lazy-loaded source tree so LookupTreeItem resolves.</summary>
    public static void WaitPlcProjectsLoaded(dynamic dte, int maxAttempts = 12, int delayMs = 250)
    {
        dynamic solution = dte.Solution;
        if ((int)solution.Projects.Count == 0)
        {
            return;
        }

        dynamic? sm = FirstSysManager(dte);
        if (sm is null)
        {
            return;
        }

        dynamic tipc = ComRetry.Invoke(() => sm.LookupTreeItem("TIPC"));
        var plcNames = new List<string>();
        for (var i = 1; i <= (int)tipc.ChildCount; i++)
        {
            plcNames.Add((string)ComRetry.Invoke(() => tipc.Child(i)).Name);
        }

        foreach (var plcName in plcNames)
        {
            var projectPath = $"TIPC^{plcName}^{plcName} Project";
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (ComRetry.Invoke(() => sm.LookupTreeItem(projectPath)) is not null)
                    {
                        break;
                    }
                }
                catch (COMException)
                {
                    // Source not loaded yet — wait and retry.
                }

                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Every ITcSysManager in the open solution (one per TwinCAT project).</summary>
    public static List<dynamic> GetSysManagers(dynamic dte, int maxAttempts = 8, int delayMs = 250)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            dynamic projects = dte.Solution.Projects;
            var count = (int)projects.Count;
            if (count > 0)
            {
                var found = new List<dynamic>();
                for (var i = 1; i <= count; i++)
                {
                    dynamic obj;
                    try
                    {
                        obj = projects.Item(i).Object;
                        if (obj is null)
                        {
                            continue;
                        }

                        obj.LookupTreeItem("TIPC");
                    }
                    catch (COMException)
                    {
                        continue;
                    }

                    found.Add(obj);
                }

                if (found.Count > 0)
                {
                    return found;
                }
            }

            Thread.Sleep(delayMs);
        }

        throw new InvalidOperationException("No TwinCAT project (ITcSysManager) found in solution.");
    }

    private static dynamic? FirstSysManager(dynamic dte)
    {
        try
        {
            return GetSysManagers(dte, maxAttempts: 1, delayMs: 0)[0];
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The ITcSysManager whose TIPC contains <paramref name="plcName"/> (or the first if empty).</summary>
    public static dynamic GetSysManager(dynamic dte, string plcName, int maxAttempts = 8, int delayMs = 250)
    {
        if (string.IsNullOrEmpty(plcName))
        {
            return GetSysManagers(dte, maxAttempts, delayMs)[0];
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            List<dynamic> managers;
            try
            {
                managers = GetSysManagers(dte, maxAttempts: 1, delayMs: 0);
            }
            catch (InvalidOperationException)
            {
                managers = [];
            }

            foreach (var sm in managers)
            {
                if (TipcHasPlc(sm, plcName))
                {
                    return sm;
                }
            }

            Thread.Sleep(delayMs);
        }

        throw new InvalidOperationException(
            $"PLC project '{plcName}' not found in any TwinCAT project under the solution.");
    }

    private static bool TipcHasPlc(dynamic sm, string plcName)
    {
        try
        {
            dynamic tipc = sm.LookupTreeItem("TIPC");
            for (var i = 1; i <= (int)tipc.ChildCount; i++)
            {
                if ((string)tipc.Child(i).Name == plcName)
                {
                    return true;
                }
            }
        }
        catch (COMException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Resolve the PLC name to operate on: explicit wins; else the sole PLC, else an error.</summary>
    public static string ResolvePlcName(dynamic dte, string? explicitName)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var names = new List<string>();
        foreach (var sm in GetSysManagers(dte))
        {
            try
            {
                dynamic tipc = sm.LookupTreeItem("TIPC");
                for (var i = 1; i <= (int)tipc.ChildCount; i++)
                {
                    names.Add((string)tipc.Child(i).Name);
                }
            }
            catch (COMException)
            {
                // Skip a project whose tree is momentarily unavailable.
            }
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException("No PLC projects under TIPC. Add one (or pass plcName explicitly).");
        }

        if (names.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple PLC projects in solution ({string.Join(", ", names)}). Pass plcName to disambiguate.");
        }

        return names[0];
    }

    public static dynamic GetPlcProjectNode(dynamic sm, string plcName)
        => ComRetry.Invoke(() => sm.LookupTreeItem($"TIPC^{plcName}^{plcName} Project"));

    public static dynamic GetPousFolder(dynamic sm, string plcName)
        => ComRetry.Invoke(() => sm.LookupTreeItem($"TIPC^{plcName}^{plcName} Project^POUs"));

    public static dynamic GetDutsFolder(dynamic sm, string plcName)
        => ComRetry.Invoke(() => sm.LookupTreeItem($"TIPC^{plcName}^{plcName} Project^DUTs"));

    /// <summary>Walk a slash-separated path of child names under a root, returning the leaf item.</summary>
    public static dynamic ResolveFolderPath(dynamic root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        var segments = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).ToList();

        // The reader reports folders WITH their type-root segment (e.g. "POUs/Drives") but resolution
        // starts AT that root, so drop one leading segment naming the root for reader/writer symmetry.
        if (segments.Count > 0 && segments[0] == (string)root.Name)
        {
            segments.RemoveAt(0);
        }

        dynamic cursor = root;
        foreach (var segment in segments)
        {
            dynamic? next = null;
            for (var i = 1; i <= (int)cursor.ChildCount; i++)
            {
                dynamic child = cursor.Child(i);
                if ((string)child.Name == segment)
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                throw new InvalidOperationException(
                    $"Path segment '{segment}' not found under '{(string)cursor.PathName}'.");
            }

            cursor = next;
        }

        return cursor;
    }

    /// <summary>Depth-first search for a tree item by name under a root.</summary>
    public static dynamic? FindChild(dynamic root, string name)
    {
        if ((string)root.Name == name)
        {
            return root;
        }

        for (var i = 1; i <= (int)root.ChildCount; i++)
        {
            var found = FindChild(root.Child(i), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>True when a POU tree item's declaration is an INTERFACE (needs the interface kinds).</summary>
    public static bool IsInterfacePou(dynamic item)
    {
        string declaration;
        try
        {
            declaration = (string)item.DeclarationText;
        }
        catch (COMException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(declaration))
        {
            return false;
        }

        var stripped = BlockComment().Replace(declaration, " ");
        stripped = LineComment().Replace(stripped, " ");
        stripped = Pragma().Replace(stripped, " ");
        var match = PouKeyword().Match(stripped);
        return match.Success && string.Equals(match.Groups[1].Value, "INTERFACE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Write declaration and/or implementation text on an item (skips empty halves).</summary>
    public static void SetItemSource(dynamic item, string? declaration, string? implementation)
    {
        if (!string.IsNullOrEmpty(declaration))
        {
            ComRetry.Invoke(() => item.DeclarationText = declaration);
        }

        if (!string.IsNullOrEmpty(implementation))
        {
            ComRetry.Invoke(() => item.ImplementationText = implementation);
        }
    }

    /// <summary>Split combined ST code and write both halves.</summary>
    public static void SetItemSourceFromCode(dynamic item, string code)
    {
        var (declaration, implementation) = StCode.Split(code);
        SetItemSource(item, declaration, implementation);
    }

    /// <summary>Flush the solution to disk (best-effort; matches the harness's post-write SaveAll).</summary>
    public static void Save(dynamic dte)
    {
        try
        {
            ComRetry.Invoke(() => dte.ExecuteCommand("File.SaveAll"));
        }
        catch (COMException)
        {
            // Best-effort: a rejected SaveAll (e.g. during a build) must not fail the write.
        }
    }

    [GeneratedRegex(@"\(\*[\s\S]*?\*\)")]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Pragma();

    [GeneratedRegex(@"(?im)\b(FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE)\b")]
    private static partial Regex PouKeyword();
}
