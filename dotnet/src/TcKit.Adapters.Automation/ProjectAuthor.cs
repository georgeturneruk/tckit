using System.Text.RegularExpressions;
using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// The authoring logic, expressed purely against the <see cref="ITcSession"/> seam: PLC/tree
/// navigation plus the create-family verbs. No COM, no threads here, so it runs against the
/// in-memory fake in CI. The COM specifics (attach, STA, retry) live in the session implementation;
/// success returns a <see cref="Result"/>, domain errors throw (the writer maps them to Result.Fail).
/// </summary>
internal static partial class ProjectAuthor
{
    public static Result OpenProject(ITcSession session, string solutionPath)
    {
        session.UseSolution(solutionPath);
        return Ok(("solution_path", solutionPath));
    }

    public static Result AddPou(
        ITcSession session, string name, PouType pouType, string code, string parentFolder, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var parent = ResolveFolderPath(PousFolder(sm, plc), parentFolder);
        var item = parent.CreateChild(name, TcKind.ForPou(pouType), null, null);
        if (!string.IsNullOrEmpty(code))
        {
            SetSourceFromCode(item, code);
        }

        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("path", item.PathName));
    }

    public static Result AddFolder(ITcSession session, string name, string parentPath, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var root = ProjectNode(sm, plc);
        var parent = ResolveFolderPath(root, string.IsNullOrEmpty(parentPath) ? "POUs" : parentPath);
        var item = parent.CreateChild(name, TcKind.Folder, null, null);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("path", item.PathName));
    }

    public static Result AddGvl(ITcSession session, string name, string code, string parentFolder, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var parent = ResolveFolderPath(PousFolder(sm, plc), parentFolder);
        var item = parent.CreateChild(name, TcKind.Gvl, null, null);
        if (!string.IsNullOrEmpty(code))
        {
            // GVLs are declaration-only; never split or write an implementation.
            item.DeclarationText = code;
        }

        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("path", item.PathName));
    }

    public static Result AddDut(
        ITcSession session, string name, string code, DutKind dutKind, string parentFolder, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var parent = ResolveFolderPath(DutsFolder(sm, plc), parentFolder);
        var item = parent.CreateChild(name, TcKind.ForDut(dutKind), null, null);
        if (!string.IsNullOrEmpty(code))
        {
            item.DeclarationText = code;
        }

        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("path", item.PathName));
    }

    public static Result AddMethod(ITcSession session, string pouName, string methodName, string code, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocatePou(sm, plc, pouName);
        var kind = IsInterfacePou(pou) ? TcKind.InterfaceMethod : TcKind.Method;
        var item = pou.CreateChild(methodName, kind, null, null);
        if (!string.IsNullOrEmpty(code))
        {
            SetSourceFromCode(item, code);
        }

        session.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc));
    }

    public static Result AddProperty(
        ITcSession session, string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode, string? plcName)
    {
        if (string.IsNullOrEmpty(getterCode) && string.IsNullOrEmpty(setterCode))
        {
            throw new ArgumentException("At least one of getterCode or setterCode must be supplied.");
        }

        var (plc, sm) = Open(session, plcName);
        var pou = LocatePou(sm, plc, pouName);
        var isInterface = IsInterfacePou(pou);

        var kindProperty = isInterface ? TcKind.InterfaceProperty : TcKind.Property;
        var kindGet = isInterface ? TcKind.InterfacePropertyGet : TcKind.PropertyGet;
        var kindSet = isInterface ? TcKind.InterfacePropertySet : TcKind.PropertySet;
        // FB property parent takes [language, type, access]; an interface property takes the type.
        object propertyVInfo = isInterface ? returnType : new[] { "ST", returnType, "PUBLIC" };

        var property = pou.CreateChild(propertyName, kindProperty, null, propertyVInfo);

        if (!string.IsNullOrEmpty(getterCode))
        {
            var get = property.CreateChild("", kindGet, null, null);
            if (!isInterface)
            {
                SetSourceFromCode(get, getterCode);
            }
        }

        if (!string.IsNullOrEmpty(setterCode))
        {
            var set = property.CreateChild("", kindSet, null, null);
            if (!isInterface)
            {
                SetSourceFromCode(set, setterCode);
            }
        }

        session.Save();
        return Ok(("pou_name", pouName), ("property_name", propertyName), ("plc_name", plc));
    }

    // --- navigation ----------------------------------------------------------

    private static (string Plc, ITcSysManager SysManager) Open(ITcSession session, string? plcName)
    {
        session.UseSolution("");
        var plc = ResolvePlcName(session, plcName);
        return (plc, GetSysManager(session, plc));
    }

    public static string ResolvePlcName(ITcSession session, string? explicitName)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var names = new List<string>();
        foreach (var sm in session.GetSysManagers())
        {
            var tipc = sm.LookupTreeItem("TIPC");
            for (var i = 1; i <= tipc.ChildCount; i++)
            {
                names.Add(tipc.Child(i).Name);
            }
        }

        return names.Count switch
        {
            0 => throw new InvalidOperationException("No PLC projects under TIPC. Add one (or pass plcName explicitly)."),
            1 => names[0],
            _ => throw new InvalidOperationException(
                $"Multiple PLC projects in solution ({string.Join(", ", names)}). Pass plcName to disambiguate."),
        };
    }

    public static ITcSysManager GetSysManager(ITcSession session, string plcName)
    {
        foreach (var sm in session.GetSysManagers())
        {
            var tipc = sm.LookupTreeItem("TIPC");
            for (var i = 1; i <= tipc.ChildCount; i++)
            {
                if (tipc.Child(i).Name == plcName)
                {
                    return sm;
                }
            }
        }

        throw new InvalidOperationException(
            $"PLC project '{plcName}' not found in any TwinCAT project under the solution.");
    }

    private static ITcTreeItem ProjectNode(ITcSysManager sm, string plc)
        => sm.LookupTreeItem($"TIPC^{plc}^{plc} Project");

    private static ITcTreeItem PousFolder(ITcSysManager sm, string plc)
        => sm.LookupTreeItem($"TIPC^{plc}^{plc} Project^POUs");

    private static ITcTreeItem DutsFolder(ITcSysManager sm, string plc)
        => sm.LookupTreeItem($"TIPC^{plc}^{plc} Project^DUTs");

    private static ITcTreeItem LocatePou(ITcSysManager sm, string plc, string pouName)
        => FindChild(PousFolder(sm, plc), pouName)
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in PLC project '{plc}'.");

    /// <summary>Walk a slash-separated path of child names under a root, returning the leaf item.</summary>
    public static ITcTreeItem ResolveFolderPath(ITcTreeItem root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        var segments = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).ToList();

        // The reader reports folders WITH their type-root segment (e.g. "POUs/Drives") but resolution
        // starts AT that root, so drop one leading segment naming the root for reader/writer symmetry.
        if (segments.Count > 0 && segments[0] == root.Name)
        {
            segments.RemoveAt(0);
        }

        var cursor = root;
        foreach (var segment in segments)
        {
            ITcTreeItem? next = null;
            for (var i = 1; i <= cursor.ChildCount; i++)
            {
                var child = cursor.Child(i);
                if (child.Name == segment)
                {
                    next = child;
                    break;
                }
            }

            cursor = next ?? throw new InvalidOperationException(
                $"Path segment '{segment}' not found under '{cursor.PathName}'.");
        }

        return cursor;
    }

    /// <summary>Depth-first search for a tree item by name under a root.</summary>
    public static ITcTreeItem? FindChild(ITcTreeItem root, string name)
    {
        if (root.Name == name)
        {
            return root;
        }

        for (var i = 1; i <= root.ChildCount; i++)
        {
            var found = FindChild(root.Child(i), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>True when a POU item's declaration is an INTERFACE (needs the interface kinds).</summary>
    public static bool IsInterfacePou(ITcTreeItem item)
    {
        var declaration = item.DeclarationText;
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

    private static void SetSourceFromCode(ITcTreeItem item, string code)
    {
        var (declaration, implementation) = StCode.Split(code);
        if (!string.IsNullOrEmpty(declaration))
        {
            item.DeclarationText = declaration;
        }

        if (!string.IsNullOrEmpty(implementation))
        {
            item.ImplementationText = implementation;
        }
    }

    private static Result Ok(params (string Key, object? Value)[] details)
        => Result.Ok(details.ToDictionary(d => d.Key, d => d.Value));

    [GeneratedRegex(@"\(\*[\s\S]*?\*\)")]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Pragma();

    [GeneratedRegex(@"(?im)\b(FUNCTION_BLOCK|FUNCTION|PROGRAM|INTERFACE)\b")]
    private static partial Regex PouKeyword();
}
