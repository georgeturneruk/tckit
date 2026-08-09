using System.Xml;
using TcKit.Core.Authoring;
using TcKit.Core.Models;

namespace TcKit.Adapters.Xml;

/// <summary>
/// The XML backend's authoring logic: every verb is a deterministic edit of the on-disk TwinCAT
/// files (ADR-0017), no COM, no running XAE. Mirrors <c>ProjectAuthor</c> verb for verb: same
/// Result detail keys (tree paths synthesised in the Automation Interface shape) and the same
/// error wording wherever the situation is the same, so tool output does not depend on the
/// selected backend. Success returns a <see cref="Result"/>; domain errors throw (the writer
/// shell maps them to Result.Fail).
/// </summary>
internal static class XmlProjectAuthor
{
    // --- create ----------------------------------------------------------------

    public static Result AddPou(SolutionContext ctx, string name, PouType pouType, string code, string parentFolder)
    {
        _ = TcKind.ForPou(pouType); // same unknown-type validation as the automation lane
        var proj = ctx.OpenPlcProj();
        var parentDir = ResolveParentFolder(ctx, proj, "POUs", parentFolder);
        EnsureNewObjectName(ctx, name);

        var (declaration, implementation) = SplitOrDefault(code, DefaultPouDeclaration(name, pouType));
        if (pouType == PouType.Interface && implementation.Length > 0)
        {
            throw new InvalidOperationException($"Interface '{name}' takes no implementation body.");
        }

        // XAE stores interfaces as .TcIO with an <Itf> root; everything else is a .TcPOU.
        var path = Path.Combine(parentDir, name + (pouType == PouType.Interface ? ".TcIO" : ".TcPOU"));
        var file = TcPlcObjectFile.CreatePou(path, name, pouType, declaration, implementation);
        file.Save();
        proj.AddCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("path", ctx.TreePathOf(path)));
    }

    public static Result AddFolder(SolutionContext ctx, string name, string parentPath)
    {
        var proj = ctx.OpenPlcProj();
        var parentDir = ResolvePath(ctx, string.IsNullOrEmpty(parentPath) ? "POUs" : parentPath);
        var dir = Path.Combine(parentDir, name);
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            throw new InvalidOperationException(
                $"Folder '{name}' already exists under '{ctx.TreePathOf(parentDir)}'.");
        }

        Directory.CreateDirectory(dir);
        EnsureFolderItems(ctx, proj, dir);
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("path", ctx.TreePathOf(dir)));
    }

    public static Result AddGvl(SolutionContext ctx, string name, string code, string parentFolder)
    {
        var proj = ctx.OpenPlcProj();
        var parentDir = ResolveParentFolder(ctx, proj, "POUs", parentFolder);
        EnsureNewObjectName(ctx, name);

        // GVLs are declaration-only; the code is the declaration, never split.
        var declaration = string.IsNullOrEmpty(code) ? "{attribute 'qualified_only'}\nVAR_GLOBAL\nEND_VAR" : code;
        var path = Path.Combine(parentDir, name + ".TcGVL");
        TcPlcObjectFile.CreateDeclarationOnly(path, "GVL", name, declaration).Save();
        proj.AddCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("path", ctx.TreePathOf(path)));
    }

    public static Result AddDut(SolutionContext ctx, string name, string code, DutKind dutKind, string parentFolder)
    {
        _ = TcKind.ForDut(dutKind); // rejects ALIAS creation, same as the automation lane
        var proj = ctx.OpenPlcProj();
        var parentDir = ResolveParentFolder(ctx, proj, "DUTs", parentFolder);
        EnsureNewObjectName(ctx, name);

        var declaration = string.IsNullOrEmpty(code) ? DefaultDutDeclaration(name, dutKind) : code;
        var path = Path.Combine(parentDir, name + ".TcDUT");
        TcPlcObjectFile.CreateDeclarationOnly(path, "DUT", name, declaration).Save();
        proj.AddCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("path", ctx.TreePathOf(path)));
    }

    public static Result AddMethod(SolutionContext ctx, string pouName, string methodName, string code)
    {
        var file = LoadPou(ctx, pouName);
        if (file.FindMember(methodName) is not null)
        {
            throw new InvalidOperationException($"Item '{methodName}' already exists on POU '{pouName}'.");
        }

        var (declaration, implementation) = SplitOrDefault(code, $"METHOD {methodName}");
        if (file.IsInterface)
        {
            if (implementation.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Interface method '{methodName}' takes no implementation body.");
            }

            file.AddMethod(methodName, declaration, implementation: null);
        }
        else
        {
            file.AddMethod(methodName, declaration, implementation);
        }

        file.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", ctx.PlcName));
    }

    public static Result AddProperty(
        SolutionContext ctx, string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode)
    {
        if (string.IsNullOrEmpty(getterCode) && string.IsNullOrEmpty(setterCode))
        {
            throw new ArgumentException("At least one of getterCode or setterCode must be supplied.");
        }

        var file = LoadPou(ctx, pouName);
        if (file.FindMember(propertyName) is not null)
        {
            throw new InvalidOperationException($"Item '{propertyName}' already exists on POU '{pouName}'.");
        }

        var property = file.AddProperty(propertyName, $"PROPERTY {propertyName} : {returnType}");
        if (!string.IsNullOrEmpty(getterCode))
        {
            AddAccessor(file, property, "Get", getterCode);
        }

        if (!string.IsNullOrEmpty(setterCode))
        {
            AddAccessor(file, property, "Set", setterCode);
        }

        file.Save();
        return Ok(("pou_name", pouName), ("property_name", propertyName), ("plc_name", ctx.PlcName));
    }

    // --- update ----------------------------------------------------------------

    public static Result UpdatePouDeclaration(SolutionContext ctx, string pouName, string code)
    {
        var file = LoadObject(ctx, pouName);
        file.Declaration = code;
        file.Save();
        return Ok(("pou_name", pouName), ("plc_name", ctx.PlcName));
    }

    public static Result UpdatePouImplementation(SolutionContext ctx, string pouName, string code)
    {
        var file = LoadObject(ctx, pouName);
        file.Implementation = code;
        file.Save();
        return Ok(("pou_name", pouName), ("plc_name", ctx.PlcName));
    }

    public static Result UpdateMethodBody(SolutionContext ctx, string pouName, string methodName, string code)
    {
        var file = LoadObject(ctx, pouName);
        var member = FindItem(file, pouName, methodName);
        SetMemberSource(file, member, methodName, code);
        file.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", ctx.PlcName));
    }

    public static Result UpdatePouDeclarationPatch(SolutionContext ctx, string pouName, string oldString, string newString)
    {
        var file = LoadObject(ctx, pouName);
        file.Declaration = PatchText.ApplyPatch(file.Declaration, oldString, newString, $"{pouName} declaration");
        file.Save();
        return Ok(("pou_name", pouName), ("plc_name", ctx.PlcName), ("replacements", 1));
    }

    public static Result UpdatePouImplementationPatch(SolutionContext ctx, string pouName, string oldString, string newString)
    {
        var file = LoadObject(ctx, pouName);
        file.Implementation = PatchText.ApplyPatch(file.Implementation, oldString, newString, $"{pouName} implementation");
        file.Save();
        return Ok(("pou_name", pouName), ("plc_name", ctx.PlcName), ("replacements", 1));
    }

    public static Result UpdateMethodBodyPatch(
        SolutionContext ctx, string pouName, string methodName, string oldString, string newString)
    {
        var file = LoadObject(ctx, pouName);
        var member = FindItem(file, pouName, methodName);
        var declaration = file.DeclarationOf(member);
        var implementation = file.ImplementationOf(member);
        var combined = string.IsNullOrEmpty(implementation) ? declaration : $"{declaration}\n{implementation}";
        var patched = PatchText.ApplyPatch(combined, oldString, newString, $"{pouName}.{methodName}");
        SetMemberSource(file, member, methodName, patched);
        file.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", ctx.PlcName), ("replacements", 1));
    }

    public static Result AddVariable(SolutionContext ctx, string pouName, string scope, string declaration, string? itemName)
    {
        var file = LoadObject(ctx, pouName);
        var target = LocateDeclItem(file, pouName, itemName);
        SetDeclarationOn(file, target, VarBlock.AddVariable(DeclarationOn(file, target), scope, declaration));
        file.Save();
        return Ok(
            ("pou_name", pouName), ("item", itemName), ("plc_name", ctx.PlcName),
            ("scope", scope.Trim().ToUpperInvariant()));
    }

    public static Result DeleteVariable(SolutionContext ctx, string pouName, string variableName, string? itemName)
    {
        var file = LoadObject(ctx, pouName);
        var target = LocateDeclItem(file, pouName, itemName);
        SetDeclarationOn(file, target, VarBlock.RemoveVariable(DeclarationOn(file, target), variableName));
        file.Save();
        return Ok(("pou_name", pouName), ("variable", variableName), ("item", itemName), ("plc_name", ctx.PlcName));
    }

    // --- delete ----------------------------------------------------------------

    public static Result DeletePou(SolutionContext ctx, string name)
    {
        var path = ctx.FindPouFile(name);
        if (path is null)
        {
            ThrowNotAPou(ctx, name);
            throw new InvalidOperationException($"POU '{name}' not found under POUs of '{ctx.PlcName}'.");
        }

        var file = TcPlcObjectFile.Load(path);
        var kind = TcKind.ForPou(TcFileParser.DetectPouType(file.Declaration, file.ContainerTag));
        if (kind == TcKind.Program)
        {
            if (TaskBinding.Find(ctx.SolutionDir, name) is { } binding)
            {
                throw new InvalidOperationException(
                    $"PROGRAM '{name}' is bound to task '{binding.Task}' in {binding.File}. Remove the PouCall first.");
            }
        }

        var proj = ctx.OpenPlcProj();
        var parentPath = ctx.TreePathOf(Path.GetDirectoryName(path)!);
        File.Delete(path);
        proj.RemoveCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("parent_path", parentPath), ("kind", kind));
    }

    public static Result DeleteMethod(SolutionContext ctx, string pouName, string methodName)
    {
        var file = LoadPou(ctx, pouName);
        var member = file.FindMember(methodName)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found under POU '{pouName}'.");
        TcPlcObjectFile.RemoveMember(member);
        file.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", ctx.PlcName));
    }

    public static Result DeleteProperty(SolutionContext ctx, string pouName, string propertyName)
    {
        var file = LoadPou(ctx, pouName);
        var member = file.FindMember(propertyName);
        if (member is null || member.LocalName != "Property")
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found under POU '{pouName}'.");
        }

        var removedAccessors = member.ChildNodes.OfType<XmlElement>().Count(e => e.LocalName is "Get" or "Set");
        TcPlcObjectFile.RemoveMember(member);
        file.Save();
        return Ok(
            ("pou_name", pouName), ("property_name", propertyName), ("plc_name", ctx.PlcName),
            ("removed_accessors", removedAccessors));
    }

    public static Result DeleteGvl(SolutionContext ctx, string name)
    {
        var path = ctx.FindGvlFile(name);
        if (path is null)
        {
            if (ctx.FindPouFile(name) is { } pouPath)
            {
                var pou = TcPlcObjectFile.Load(pouPath);
                var pouKind = TcKind.ForPou(TcFileParser.DetectPouType(pou.Declaration, pou.ContainerTag));
                throw new InvalidOperationException(
                    $"'{name}' is not a GVL (kind={pouKind}, expected {TcKind.Gvl}). Use delete_pou / delete_folder / delete_dut.");
            }

            throw new InvalidOperationException($"GVL '{name}' not found under POUs of '{ctx.PlcName}'.");
        }

        var proj = ctx.OpenPlcProj();
        var parentPath = ctx.TreePathOf(Path.GetDirectoryName(path)!);
        File.Delete(path);
        proj.RemoveCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("parent_path", parentPath), ("kind", TcKind.Gvl));
    }

    public static Result DeleteDut(SolutionContext ctx, string name)
    {
        var path = ctx.FindDutFile(name)
            ?? throw new InvalidOperationException($"DUT '{name}' not found under DUTs of '{ctx.PlcName}'.");

        var kind = TcKind.ForDutItem(TcFileParser.ClassifyDut(TcPlcObjectFile.Load(path).Declaration));
        var proj = ctx.OpenPlcProj();
        var parentPath = ctx.TreePathOf(Path.GetDirectoryName(path)!);
        File.Delete(path);
        proj.RemoveCompileItem(ctx.IncludeFor(path));
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("parent_path", parentPath), ("kind", kind));
    }

    public static Result DeleteFolder(SolutionContext ctx, string name, string parentPath, bool recursive)
    {
        string? dir;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parent = ctx.PlcDir;
            foreach (var segment in parentPath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                parent = Path.Combine(parent, segment);
                if (!Directory.Exists(parent))
                {
                    throw new InvalidOperationException(
                        $"Parent path segment '{segment}' not found under PLC project '{ctx.PlcName}'.");
                }
            }

            dir = Path.Combine(parent, name);
            if (File.Exists(dir))
            {
                // A file of that name is a tree item of a non-folder kind.
                throw NotAFolder(dir, name);
            }

            if (!Directory.Exists(dir))
            {
                dir = null;
            }
        }
        else
        {
            dir = Directory.EnumerateDirectories(ctx.PlcDir, name, SearchOption.AllDirectories)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (dir is null)
        {
            throw new InvalidOperationException($"Folder '{name}' not found under PLC project '{ctx.PlcName}'.");
        }

        var entries = Directory.EnumerateFileSystemEntries(dir).Count();
        if (entries > 0 && !recursive)
        {
            throw new InvalidOperationException(
                $"Folder '{name}' is not empty (contains {entries} item(s)); pass recursive=true to cascade.");
        }

        var proj = ctx.OpenPlcProj();
        var reported = ctx.TreePathOf(Path.GetDirectoryName(dir)!);
        proj.RemoveItemsUnder(ctx.IncludeFor(dir));
        Directory.Delete(dir, recursive: true);
        proj.Save();
        return Ok(("name", name), ("plc_name", ctx.PlcName), ("parent_path", reported));
    }

    // --- library references / placeholders --------------------------------------

    public static Result AddLibraryReference(
        SolutionContext ctx, string libraryName, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters)
    {
        if (string.IsNullOrEmpty(libraryName))
        {
            throw new ArgumentException("LibraryName required.");
        }

        var proj = ctx.OpenPlcProj();
        if (proj.HasReference(PlcProjXml.LibraryElement, libraryName))
        {
            throw new InvalidOperationException(
                $"Library reference '{libraryName}' is already contained in '{ctx.PlcName}'.");
        }

        proj.AddLibraryReference(libraryName, version, distributor);
        proj.Save();

        if (parameters is { Count: > 0 })
        {
            PlcProjXml.SetReferenceParameters(ctx.PlcProjPath, PlcProjXml.LibraryElement, libraryName, parameters);
        }

        return Ok(
            ("consumer_plc", ctx.PlcName), ("library", libraryName), ("version", version),
            ("distributor", distributor));
    }

    public static Result DeleteLibraryReference(
        SolutionContext ctx, string libraryName, string version, string distributor)
    {
        if (string.IsNullOrEmpty(libraryName))
        {
            throw new ArgumentException("LibraryName required.");
        }

        var proj = ctx.OpenPlcProj();
        var resolvedVersion = proj.RemoveLibraryReference(libraryName, version, distributor)
            ?? throw new InvalidOperationException(
                $"No library reference matching name='{libraryName}' distributor='{distributor}' found on '{ctx.PlcName}'.");
        proj.Save();
        return Ok(
            ("consumer_plc", ctx.PlcName), ("library", libraryName), ("version", resolvedVersion),
            ("distributor", distributor));
    }

    public static Result AddLibraryPlaceholder(
        SolutionContext ctx, string placeholderName, string defaultLibrary, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters)
    {
        if (string.IsNullOrEmpty(placeholderName))
        {
            throw new ArgumentException("PlaceholderName required.");
        }

        if (string.IsNullOrEmpty(defaultLibrary))
        {
            throw new ArgumentException("DefaultLibrary required.");
        }

        var proj = ctx.OpenPlcProj();
        var alreadyPresent = proj.HasReference(PlcProjXml.PlaceholderElement, placeholderName);
        if (!alreadyPresent)
        {
            proj.AddPlaceholder(placeholderName, defaultLibrary, version, distributor);
            proj.Save();
        }

        if (parameters is { Count: > 0 })
        {
            PlcProjXml.SetReferenceParameters(
                ctx.PlcProjPath, PlcProjXml.PlaceholderElement, placeholderName, parameters);
        }

        return Ok(
            ("consumer_plc", ctx.PlcName), ("placeholder", placeholderName), ("default_library", defaultLibrary),
            ("version", version), ("distributor", distributor), ("already_present", alreadyPresent));
    }

    public static Result SetPlaceholderParameters(
        SolutionContext ctx, string placeholderName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        if (string.IsNullOrEmpty(placeholderName))
        {
            throw new ArgumentException("PlaceholderName required.");
        }

        if (parameters is null || parameters.Count == 0)
        {
            throw new ArgumentException("Parameters required.");
        }

        PlcProjXml.SetPlaceholderParameters(ctx.PlcProjPath, placeholderName, parameters);
        return Ok(("consumer_plc", ctx.PlcName), ("placeholder", placeholderName));
    }

    public static Result DeletePlaceholder(SolutionContext ctx, string placeholderName)
    {
        if (string.IsNullOrEmpty(placeholderName))
        {
            throw new ArgumentException("PlaceholderName required.");
        }

        var proj = ctx.OpenPlcProj();
        if (!proj.RemovePlaceholder(placeholderName))
        {
            throw new InvalidOperationException($"Placeholder '{placeholderName}' not found on '{ctx.PlcName}'.");
        }

        proj.Save();
        return Ok(("consumer_plc", ctx.PlcName), ("placeholder", placeholderName));
    }

    // --- navigation and shared helpers -------------------------------------------

    private static TcPlcObjectFile LoadPou(SolutionContext ctx, string pouName)
    {
        var path = ctx.FindPouFile(pouName)
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in PLC project '{ctx.PlcName}'.");
        return TcPlcObjectFile.Load(path);
    }

    /// <summary>Any object file by name, POUs first (the update verbs work project-wide).</summary>
    private static TcPlcObjectFile LoadObject(SolutionContext ctx, string pouName)
    {
        var path = ctx.FindObjectFile(pouName)
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in PLC project '{ctx.PlcName}'.");
        return TcPlcObjectFile.Load(path);
    }

    private static XmlElement FindItem(TcPlcObjectFile file, string pouName, string itemName)
        => file.FindMember(itemName)
            ?? throw new InvalidOperationException($"Item '{itemName}' not found on POU '{pouName}'.");

    /// <summary>The declaration-bearing target: the container itself, or a named member under it.</summary>
    private static XmlElement? LocateDeclItem(TcPlcObjectFile file, string pouName, string? itemName)
        => string.IsNullOrEmpty(itemName) || itemName == pouName ? null : FindItem(file, pouName, itemName);

    private static string DeclarationOn(TcPlcObjectFile file, XmlElement? member)
        => member is null ? file.Declaration : file.DeclarationOf(member);

    private static void SetDeclarationOn(TcPlcObjectFile file, XmlElement? member, string text)
    {
        if (member is null)
        {
            file.Declaration = text;
        }
        else
        {
            file.SetDeclarationOn(member, text);
        }
    }

    /// <summary>The SetSourceFromCode mirror: split, then write only the non-empty halves.</summary>
    private static void SetMemberSource(TcPlcObjectFile file, XmlElement member, string itemName, string code)
    {
        var (declaration, implementation) = StCode.Split(code);
        if (!string.IsNullOrEmpty(declaration))
        {
            file.SetDeclarationOn(member, declaration);
        }

        if (!string.IsNullOrEmpty(implementation))
        {
            if (file.IsInterface)
            {
                throw new InvalidOperationException($"Interface member '{itemName}' takes no implementation body.");
            }

            file.SetImplementationOn(member, implementation);
        }
    }

    private static void AddAccessor(TcPlcObjectFile file, XmlElement property, string kind, string code)
    {
        if (file.IsInterface)
        {
            // Interface accessors are bare markers; the property declaration carries the type.
            file.AddAccessor(property, kind, "", implementation: null);
            return;
        }

        // The accessor code is the body, optionally preceded by a local VAR block (no header).
        var (declaration, implementation) = StCode.Split(code);
        file.AddAccessor(
            property, kind,
            string.IsNullOrEmpty(declaration) ? "VAR\nEND_VAR" : declaration,
            implementation);
    }

    /// <summary>Resolve (and auto-create) the type root, then walk the parent folder path under it.</summary>
    private static string ResolveParentFolder(SolutionContext ctx, PlcProjFile proj, string rootName, string parentFolder)
    {
        var rootDir = Path.Combine(ctx.PlcDir, rootName);
        if (!Directory.Exists(rootDir))
        {
            // XAE-scaffolded PLCs always have the POUs / DUTs roots; recreate the standard shape
            // for hand-rolled trees rather than refusing.
            Directory.CreateDirectory(rootDir);
            if (!proj.HasFolderItem(rootName))
            {
                proj.AddFolderItem(rootName);
            }
        }

        if (string.IsNullOrEmpty(parentFolder))
        {
            return rootDir;
        }

        var segments = parentFolder.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        // The reader reports folders WITH their type-root segment (e.g. "POUs/Drives") but
        // resolution starts AT that root; drop one leading segment naming the root (ADR-0013).
        if (segments.Count > 0 && segments[0] == rootName)
        {
            segments.RemoveAt(0);
        }

        var dir = rootDir;
        foreach (var segment in segments)
        {
            var next = Path.Combine(dir, segment);
            if (!Directory.Exists(next))
            {
                throw new InvalidOperationException(
                    $"Path segment '{segment}' not found under '{ctx.TreePathOf(dir)}'.");
            }

            dir = next;
        }

        return dir;
    }

    /// <summary>Walk a project-root-relative path where every segment must already exist.</summary>
    private static string ResolvePath(SolutionContext ctx, string path)
    {
        var dir = ctx.PlcDir;
        foreach (var segment in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(dir, segment);
            if (!Directory.Exists(next))
            {
                throw new InvalidOperationException(
                    $"Path segment '{segment}' not found under '{ctx.TreePathOf(dir)}'.");
            }

            dir = next;
        }

        return dir;
    }

    /// <summary>Make sure the folder (and its ancestors up to the PLC root) carry Folder items.</summary>
    private static void EnsureFolderItems(SolutionContext ctx, PlcProjFile proj, string dir)
    {
        for (var cursor = dir;
            cursor is not null && !PathsEqual(cursor, ctx.PlcDir);
            cursor = Path.GetDirectoryName(cursor))
        {
            var include = ctx.IncludeFor(cursor);
            if (!proj.HasFolderItem(include))
            {
                proj.AddFolderItem(include);
            }
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void EnsureNewObjectName(SolutionContext ctx, string name)
    {
        if (ctx.FindObjectFile(name) is not null)
        {
            throw new InvalidOperationException(
                $"'{name}' already exists in PLC project '{ctx.PlcName}'.");
        }
    }

    /// <summary>Kind-specific refusal when a delete_pou target is really a GVL or a folder.</summary>
    private static void ThrowNotAPou(SolutionContext ctx, string name)
    {
        if (ctx.FindGvlFile(name) is not null)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a POU (kind={TcKind.Gvl}). Use delete_folder / delete_gvl / delete_dut.");
        }

        if (Directory.EnumerateDirectories(ctx.PlcDir, name, SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException(
                $"'{name}' is not a POU (kind={TcKind.Folder}). Use delete_folder / delete_gvl / delete_dut.");
        }
    }

    private static InvalidOperationException NotAFolder(string path, string name)
    {
        var kind = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".TCGVL" => TcKind.Gvl,
            ".TCDUT" => TcKind.Struct,
            _ => TcKind.FunctionBlock,
        };
        return new InvalidOperationException(
            $"'{name}' is not a folder (kind={kind}, expected {TcKind.Folder}). Use delete_pou / delete_gvl / delete_dut.");
    }

    private static (string Declaration, string Implementation) SplitOrDefault(string code, string defaultDeclaration)
    {
        if (string.IsNullOrEmpty(code))
        {
            return (defaultDeclaration, "");
        }

        return StCode.Split(code);
    }

    private static string DefaultPouDeclaration(string name, PouType pouType) => pouType switch
    {
        PouType.Program => $"PROGRAM {name}\nVAR\nEND_VAR",
        PouType.Function => $"FUNCTION {name} : BOOL\nVAR_INPUT\nEND_VAR\nVAR\nEND_VAR",
        PouType.FunctionBlock => $"FUNCTION_BLOCK {name}\nVAR_INPUT\nEND_VAR\nVAR_OUTPUT\nEND_VAR\nVAR\nEND_VAR",
        PouType.Interface => $"INTERFACE {name}",
        _ => throw new ArgumentOutOfRangeException(nameof(pouType), pouType, "Unknown POU type."),
    };

    private static string DefaultDutDeclaration(string name, DutKind dutKind) => dutKind switch
    {
        DutKind.Struct => $"TYPE {name} :\nSTRUCT\nEND_STRUCT\nEND_TYPE",
        DutKind.Enum => $"TYPE {name} :\n(\n);\nEND_TYPE",
        DutKind.Union => $"TYPE {name} :\nUNION\nEND_UNION\nEND_TYPE",
        _ => throw new ArgumentOutOfRangeException(nameof(dutKind), dutKind, "Unknown DUT kind."),
    };

    private static Result Ok(params (string Key, object? Value)[] details)
        => Result.Ok(details.ToDictionary(d => d.Key, d => d.Value));
}
