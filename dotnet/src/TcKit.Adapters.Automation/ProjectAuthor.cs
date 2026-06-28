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

    // --- update --------------------------------------------------------------

    public static Result UpdatePouDeclaration(ITcSession session, string pouName, string code, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        LocatePouUnderProject(sm, plc, pouName).DeclarationText = code;
        session.Save();
        return Ok(("pou_name", pouName), ("plc_name", plc));
    }

    public static Result UpdatePouImplementation(ITcSession session, string pouName, string code, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        LocatePouUnderProject(sm, plc, pouName).ImplementationText = code;
        session.Save();
        return Ok(("pou_name", pouName), ("plc_name", plc));
    }

    public static Result UpdateMethodBody(ITcSession session, string pouName, string methodName, string code, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateItem(sm, plc, pouName, methodName);
        SetSourceFromCode(item, code);
        session.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc));
    }

    public static Result UpdatePouDeclarationPatch(
        ITcSession session, string pouName, string oldString, string newString, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocatePouUnderProject(sm, plc, pouName);
        pou.DeclarationText = ApplyPatch(pou.DeclarationText, oldString, newString, $"{pouName} declaration");
        session.Save();
        return Ok(("pou_name", pouName), ("plc_name", plc), ("replacements", 1));
    }

    public static Result UpdatePouImplementationPatch(
        ITcSession session, string pouName, string oldString, string newString, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocatePouUnderProject(sm, plc, pouName);
        pou.ImplementationText = ApplyPatch(pou.ImplementationText, oldString, newString, $"{pouName} implementation");
        session.Save();
        return Ok(("pou_name", pouName), ("plc_name", plc), ("replacements", 1));
    }

    public static Result UpdateMethodBodyPatch(
        ITcSession session, string pouName, string methodName, string oldString, string newString, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateItem(sm, plc, pouName, methodName);
        var patched = ApplyPatch(CombineSource(item), oldString, newString, $"{pouName}.{methodName}");
        SetSourceFromCode(item, patched);
        session.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc), ("replacements", 1));
    }

    // --- delete --------------------------------------------------------------

    private static readonly int[] s_pouKinds = [TcKind.Program, TcKind.Function, TcKind.FunctionBlock, TcKind.Interface];
    private static readonly int[] s_dutKinds = [TcKind.Struct, TcKind.Enum, TcKind.Union, TcKind.Alias];

    public static Result DeletePou(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pous = PousFolder(sm, plc);
        var item = LocateUnderFolder(pous, name, "POU", plc);
        if (!s_pouKinds.Contains(item.ItemType))
        {
            throw new InvalidOperationException(
                $"'{name}' is not a POU (kind={item.ItemType}). Use delete_folder / delete_gvl / delete_dut.");
        }

        if (item.ItemType == TcKind.Program)
        {
            var solutionDir = Path.GetDirectoryName(session.SolutionPath);
            if (!string.IsNullOrEmpty(solutionDir) && TaskBinding.Find(solutionDir, name) is { } binding)
            {
                throw new InvalidOperationException(
                    $"PROGRAM '{name}' is bound to task '{binding.Task}' in {binding.File}. Remove the PouCall first.");
            }
        }

        var parentPath = Remove(sm, item);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", item.ItemType));
    }

    public static Result DeleteMethod(ITcSession session, string pouName, string methodName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocateUnderFolder(PousFolder(sm, plc), pouName, "POU", plc);
        var method = FindChild(pou, methodName);
        if (method is null || method.Name == pou.Name)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found under POU '{pouName}'.");
        }

        pou.DeleteChild(methodName);
        session.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc));
    }

    public static Result DeleteProperty(ITcSession session, string pouName, string propertyName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocateUnderFolder(PousFolder(sm, plc), pouName, "POU", plc);
        var property = FindChild(pou, propertyName);
        if (property is null || property.Name == pou.Name)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found under POU '{pouName}'.");
        }

        var removed = RemovePropertyNode(pou, property, propertyName);
        session.Save();
        return Ok(("pou_name", pouName), ("property_name", propertyName), ("plc_name", plc), ("removed_accessors", removed));
    }

    public static Result DeleteGvl(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateUnderFolder(PousFolder(sm, plc), name, "GVL", plc);
        if (item.ItemType != TcKind.Gvl)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a GVL (kind={item.ItemType}, expected {TcKind.Gvl}). Use delete_pou / delete_folder / delete_dut.");
        }

        var parentPath = Remove(sm, item);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", item.ItemType));
    }

    public static Result DeleteDut(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateUnderFolder(DutsFolder(sm, plc), name, "DUT", plc);
        if (!s_dutKinds.Contains(item.ItemType))
        {
            throw new InvalidOperationException(
                $"'{name}' is not a DUT (kind={item.ItemType}). Use delete_pou / delete_gvl / delete_folder.");
        }

        var parentPath = Remove(sm, item);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", item.ItemType));
    }

    public static Result DeleteFolder(
        ITcSession session, string name, string parentPath, bool recursive, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var plcProject = ProjectNode(sm, plc);

        ITcTreeItem? folder;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parent = plcProject;
            foreach (var segment in parentPath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                parent = FindChild(parent, segment)
                    ?? throw new InvalidOperationException(
                        $"Parent path segment '{segment}' not found under PLC project '{plc}'.");
            }

            folder = DirectChild(parent, name);
        }
        else
        {
            folder = FindChild(plcProject, name);
            if (folder is not null && folder.Name == plcProject.Name)
            {
                folder = null;
            }
        }

        if (folder is null)
        {
            throw new InvalidOperationException($"Folder '{name}' not found under PLC project '{plc}'.");
        }

        if (folder.ItemType != TcKind.Folder)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a folder (kind={folder.ItemType}, expected {TcKind.Folder}). Use delete_pou / delete_gvl / delete_dut.");
        }

        if (folder.ChildCount > 0 && !recursive)
        {
            throw new InvalidOperationException(
                $"Folder '{name}' is not empty (contains {folder.ChildCount} item(s)); pass recursive=true to cascade.");
        }

        // DeleteChild shifts indices, so always drain the first child until empty.
        while (folder.ChildCount > 0)
        {
            folder.DeleteChild(folder.Child(1).Name);
        }

        var reported = Remove(sm, folder);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", reported));
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

    private static ITcTreeItem LocatePouUnderProject(ITcSysManager sm, string plc, string pouName)
        => FindChild(ProjectNode(sm, plc), pouName)
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in PLC project '{plc}'.");

    private static ITcTreeItem LocateItem(ITcSysManager sm, string plc, string pouName, string itemName)
    {
        var pou = LocatePouUnderProject(sm, plc, pouName);
        return FindChild(pou, itemName)
            ?? throw new InvalidOperationException($"Item '{itemName}' not found on POU '{pouName}'.");
    }

    /// <summary>Find an item by name under a type-root folder, rejecting the folder itself.</summary>
    private static ITcTreeItem LocateUnderFolder(ITcTreeItem folder, string name, string label, string plc)
    {
        var item = FindChild(folder, name);
        if (item is null || item.Name == folder.Name)
        {
            throw new InvalidOperationException($"{label} '{name}' not found under {folder.Name} of '{plc}'.");
        }

        return item;
    }

    /// <summary>Delete a tree item by resolving its parent from its PathName and calling DeleteChild.</summary>
    private static string Remove(ITcSysManager sm, ITcTreeItem item)
    {
        var segments = item.PathName.Split('^');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException($"Cannot resolve parent of '{item.Name}' (PathName={item.PathName}).");
        }

        var parentPath = string.Join('^', segments[..^1]);
        sm.LookupTreeItem(parentPath).DeleteChild(item.Name);
        return parentPath;
    }

    /// <summary>Delete a property's Get/Set accessors (best-effort) then the property body.</summary>
    private static IReadOnlyList<string> RemovePropertyNode(ITcTreeItem pou, ITcTreeItem property, string propertyName)
    {
        var removed = new List<string>();
        foreach (var accessor in new[] { "Get", "Set" })
        {
            if (DirectChild(property, accessor) is null)
            {
                continue;
            }

            try
            {
                property.DeleteChild(accessor);
                removed.Add(accessor);
            }
#pragma warning disable CA1031 // Some XAE versions cascade-delete accessors with the property; tolerate.
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        pou.DeleteChild(propertyName);
        return removed;
    }

    private static ITcTreeItem? DirectChild(ITcTreeItem parent, string name)
    {
        for (var i = 1; i <= parent.ChildCount; i++)
        {
            var child = parent.Child(i);
            if (child.Name == name)
            {
                return child;
            }
        }

        return null;
    }

    private static string CombineSource(ITcTreeItem item)
    {
        var implementation = item.ImplementationText;
        return string.IsNullOrEmpty(implementation) ? item.DeclarationText : $"{item.DeclarationText}\n{implementation}";
    }

    /// <summary>Anchored single-occurrence replacement, mirroring Claude Code's Edit semantics.</summary>
    private static string ApplyPatch(string text, string oldString, string newString, string where)
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw new ArgumentException("OldString required.");
        }

        var count = 0;
        for (var i = text.IndexOf(oldString, StringComparison.Ordinal); i >= 0;
            i = text.IndexOf(oldString, i + oldString.Length, StringComparison.Ordinal))
        {
            count++;
        }

        if (count == 0)
        {
            throw new InvalidOperationException($"OldString not found in {where}.");
        }

        if (count > 1)
        {
            throw new InvalidOperationException(
                $"OldString appears {count} times in {where}; anchor must be unique. Extend OldString with more surrounding context.");
        }

        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        return text[..index] + newString + text[(index + oldString.Length)..];
    }

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
