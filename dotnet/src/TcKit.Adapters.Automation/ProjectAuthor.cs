using System.Text.RegularExpressions;
using System.Xml;
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

    public static Result AddVariable(
        ITcSession session, string pouName, string scope, string declaration, string? itemName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateDeclItem(sm, plc, pouName, itemName);
        item.DeclarationText = VarBlock.AddVariable(item.DeclarationText, scope, declaration);
        session.Save();
        return Ok(("pou_name", pouName), ("item", itemName), ("plc_name", plc), ("scope", scope.Trim().ToUpperInvariant()));
    }

    public static Result DeleteVariable(
        ITcSession session, string pouName, string variableName, string? itemName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateDeclItem(sm, plc, pouName, itemName);
        item.DeclarationText = VarBlock.RemoveVariable(item.DeclarationText, variableName);
        session.Save();
        return Ok(("pou_name", pouName), ("variable", variableName), ("item", itemName), ("plc_name", plc));
    }

    // --- delete --------------------------------------------------------------

    private static readonly int[] s_pouKinds = [TcKind.Program, TcKind.Function, TcKind.FunctionBlock, TcKind.Interface];
    private static readonly int[] s_dutKinds = [TcKind.Struct, TcKind.Enum, TcKind.Union, TcKind.Alias];

    public static Result DeletePou(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateUnderFolder(PousFolder(sm, plc), name, "POU", plc);
        // Capture from the fresh handle before any further tree navigation invalidates it.
        var (kind, pathName) = (item.ItemType, item.PathName);
        if (!s_pouKinds.Contains(kind))
        {
            throw new InvalidOperationException(
                $"'{name}' is not a POU (kind={kind}). Use delete_folder / delete_gvl / delete_dut.");
        }

        if (kind == TcKind.Program)
        {
            var solutionDir = Path.GetDirectoryName(session.SolutionPath);
            if (!string.IsNullOrEmpty(solutionDir) && TaskBinding.Find(solutionDir, name) is { } binding)
            {
                throw new InvalidOperationException(
                    $"PROGRAM '{name}' is bound to task '{binding.Task}' in {binding.File}. Remove the PouCall first.");
            }
        }

        var parentPath = Remove(sm, pathName);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", kind));
    }

    public static Result DeleteMethod(ITcSession session, string pouName, string methodName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocateUnderFolder(PousFolder(sm, plc), pouName, "POU", plc);
        var pouPath = pou.PathName;
        var method = FindChild(pou, methodName);
        if (method is null || method.PathName == pouPath)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found under POU '{pouName}'.");
        }

        // Re-resolve the POU fresh: FindChild navigated its subtree, which can stale the handle.
        sm.LookupTreeItem(pouPath).DeleteChild(methodName);
        session.Save();
        return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc));
    }

    public static Result DeleteProperty(ITcSession session, string pouName, string propertyName, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var pou = LocateUnderFolder(PousFolder(sm, plc), pouName, "POU", plc);
        var pouPath = pou.PathName;
        var property = FindChild(pou, propertyName);
        if (property is null || property.PathName == pouPath)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found under POU '{pouName}'.");
        }

        var removed = RemovePropertyNode(sm, pouPath, property.PathName, propertyName);
        session.Save();
        return Ok(("pou_name", pouName), ("property_name", propertyName), ("plc_name", plc), ("removed_accessors", removed));
    }

    public static Result DeleteGvl(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateUnderFolder(PousFolder(sm, plc), name, "GVL", plc);
        var (kind, pathName) = (item.ItemType, item.PathName);
        if (kind != TcKind.Gvl)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a GVL (kind={kind}, expected {TcKind.Gvl}). Use delete_pou / delete_folder / delete_dut.");
        }

        var parentPath = Remove(sm, pathName);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", kind));
    }

    public static Result DeleteDut(ITcSession session, string name, string? plcName)
    {
        var (plc, sm) = Open(session, plcName);
        var item = LocateUnderFolder(DutsFolder(sm, plc), name, "DUT", plc);
        var (kind, pathName) = (item.ItemType, item.PathName);
        if (!s_dutKinds.Contains(kind))
        {
            throw new InvalidOperationException(
                $"'{name}' is not a DUT (kind={kind}). Use delete_pou / delete_gvl / delete_folder.");
        }

        var parentPath = Remove(sm, pathName);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", parentPath), ("kind", kind));
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

        var (folderKind, folderPath) = (folder.ItemType, folder.PathName);
        if (folderKind != TcKind.Folder)
        {
            throw new InvalidOperationException(
                $"'{name}' is not a folder (kind={folderKind}, expected {TcKind.Folder}). Use delete_pou / delete_gvl / delete_dut.");
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

        var reported = Remove(sm, folderPath);
        session.Save();
        return Ok(("name", name), ("plc_name", plc), ("parent_path", reported));
    }

    // --- library references / placeholders -----------------------------------

    public static Result AddLibraryReference(
        ITcSession session, string? plcName, string libraryName, string version, string distributor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters = null)
    {
        if (string.IsNullOrEmpty(libraryName))
        {
            throw new ArgumentException("LibraryName required.");
        }

        var (plc, sm) = Open(session, plcName);
        ReferencesNode(sm, plc).AddLibrary(libraryName, version, distributor);
        // AddLibrary mutates only in-memory state; Save persists the .plcproj.
        session.Save();

        if (parameters is { Count: > 0 })
        {
            var plcProjPath = PlcProjXml.Find(Path.GetDirectoryName(session.SolutionPath), plc);
            SpliceParameters(session, plcProjPath, PlcProjXml.LibraryElement, libraryName, parameters);
        }

        return Ok(
            ("consumer_plc", plc), ("library", libraryName), ("version", version), ("distributor", distributor));
    }

    public static Result DeleteLibraryReference(
        ITcSession session, string? plcName, string libraryName, string version, string distributor)
    {
        if (string.IsNullOrEmpty(libraryName))
        {
            throw new ArgumentException("LibraryName required.");
        }

        var (plc, sm) = Open(session, plcName);
        var refsPath = ReferencesNode(sm, plc).PathName;

        // AddLibrary with "*" keeps "*" as the declared Version, but the 3-arg RemoveReference only
        // matches against the resolved EffectiveVersion. When the caller passes "*", enumerate the
        // References children and read EffectiveVersion off the matching entry's ProduceXml.
        var resolvedVersion = version;
        if (string.IsNullOrEmpty(version) || version == "*")
        {
            resolvedVersion = ResolveEffectiveVersion(sm.LookupTreeItem(refsPath), libraryName, distributor)
                ?? throw new InvalidOperationException(
                    $"No library reference matching name='{libraryName}' distributor='{distributor}' "
                    + $"(with a resolved EffectiveVersion) found on '{plc}'.");
        }

        sm.LookupTreeItem(refsPath).RemoveReference(libraryName, resolvedVersion, distributor);
        session.Save();
        if (TryFindPlcProj(session.SolutionPath, plc) is { } plcProjPath)
        {
            ParameterGuard.Unregister(plcProjPath, libraryName);
        }

        return Ok(
            ("consumer_plc", plc), ("library", libraryName), ("version", resolvedVersion), ("distributor", distributor));
    }

    public static Result AddLibraryPlaceholder(
        ITcSession session, string? plcName, string placeholderName, string defaultLibrary,
        string version, string distributor,
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

        var (plc, sm) = Open(session, plcName);
        var solutionPath = session.SolutionPath;

        // Idempotency probe: AddPlaceholder throws "already contained!" on a duplicate, so check the
        // on-disk .plcproj first and fall straight through to the parameter splice on a hit.
        string? plcProjPath = TryFindPlcProj(solutionPath, plc);
        var alreadyPresent = plcProjPath is not null && PlcProjXml.HasPlaceholder(plcProjPath, placeholderName);

        if (!alreadyPresent)
        {
            ReferencesNode(sm, plc).AddPlaceholder(placeholderName, defaultLibrary, version, distributor);
            session.Save();
        }

        if (parameters is { Count: > 0 })
        {
            plcProjPath ??= PlcProjXml.Find(Path.GetDirectoryName(solutionPath), plc);
            SpliceParameters(session, plcProjPath, PlcProjXml.PlaceholderElement, placeholderName, parameters);
        }

        return Ok(
            ("consumer_plc", plc), ("placeholder", placeholderName), ("default_library", defaultLibrary),
            ("version", version), ("distributor", distributor), ("already_present", alreadyPresent));
    }

    public static Result SetPlaceholderParameters(
        ITcSession session, string? plcName, string placeholderName,
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

        var (plc, _) = Open(session, plcName);
        var plcProjPath = PlcProjXml.Find(Path.GetDirectoryName(session.SolutionPath), plc);

        SpliceParameters(session, plcProjPath, PlcProjXml.PlaceholderElement, placeholderName, parameters);

        return Ok(("consumer_plc", plc), ("placeholder", placeholderName));
    }

    public static Result DeletePlaceholder(ITcSession session, string? plcName, string placeholderName)
    {
        if (string.IsNullOrEmpty(placeholderName))
        {
            throw new ArgumentException("PlaceholderName required.");
        }

        var (plc, sm) = Open(session, plcName);
        // Single-arg RemoveReference targets placeholders specifically.
        ReferencesNode(sm, plc).RemoveReference(placeholderName);
        session.Save();
        if (TryFindPlcProj(session.SolutionPath, plc) is { } plcProjPath)
        {
            ParameterGuard.Unregister(plcProjPath, placeholderName);
        }

        return Ok(("consumer_plc", plc), ("placeholder", placeholderName));
    }

    public static Result SavePlcAsLibrary(
        ITcSession session, string? plcName, string outputPath, bool install, string repository, bool overwrite)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentException("OutputPath required.");
        }

        if (install && repository != "System")
        {
            throw new InvalidOperationException(
                $"Repository '{repository}' not yet supported; v1 supports only 'System'. "
                + "Pass install=false to skip install.");
        }

        var (plc, sm) = Open(session, plcName);

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        // SaveAsLibrary refuses to overwrite; honour overwrite by removing first.
        if (overwrite && File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var projectPath = ProjectNode(sm, plc).PathName;
        var title = plc;
        const string company = "Tc3 Project";
        const string version = "1.0.0.0";

        // SaveAsLibrary refuses a managed library with an empty ProjectInfo/Title (the Standard
        // template leaves it blank), so set Title/Company/Version via the ProduceXml/ConsumeXml
        // round-trip before SaveAsLibrary.
        void MetadataAndSave()
        {
            var node = sm.LookupTreeItem(projectPath);
            node.ConsumeXml(WithProjectInfo(node.ProduceXml(0), title, company, version));
            sm.LookupTreeItem(projectPath).SaveAsLibrary(outputPath, install);
        }

        // Cold-start recovery: on a fresh XAE the placeholder resolver hasn't run, so ProduceXml
        // chokes with an XmlAutomationException naming PlaceholderReference/EffectiveResolution.
        // CheckAllObjects (an in-process compile) runs the resolver as a side effect; retry once.
        var coldStartWarmup = false;
        try
        {
            MetadataAndSave();
        }
        catch (Exception exc) when (IsColdStartResolverError(exc.Message))
        {
            try
            {
                sm.LookupTreeItem(projectPath).CheckAllObjects();
            }
#pragma warning disable CA1031 // Rethrow with the headless-mode hint (the only actionable cause).
            catch (Exception warmupExc)
            {
                throw new InvalidOperationException(
                    $"save_plc_as_library cold-start retry failed during warm-up build: {warmupExc.Message}. "
                    + "A headless XAE instance is known-incompatible with cold-start save "
                    + "(Microsoft Visual Studio Appid Stub SyncLock); open the solution in TcXaeShell and retry.");
            }
#pragma warning restore CA1031

            MetadataAndSave();
            coldStartWarmup = true;
        }

        session.Save();
        return Ok(
            ("plc", plc), ("output_path", outputPath), ("installed", install),
            ("repository", install ? repository : null), ("title", title), ("company", company),
            ("version", version), ("cold_start_warmup", coldStartWarmup));
    }

    // --- project scaffolding -------------------------------------------------

    public static Result CreateProject(ITcSession session, string name, string path)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Name required.");
        }

        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path required.");
        }

        var template = ResolveTemplatePath();
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        var plcName = $"{name}_Plc";
        session.CreateSolution(path, name);
        session.AddProjectFromTemplate(template, Path.Combine(path, name), name);

        var tipc = FindEmptyTipc(session);
        tipc.CreateChild(plcName, 0, null, "Standard PLC Template.plcproj");

        var solutionPath = Path.Combine(path, $"{name}.sln");
        session.SaveSolutionAs(solutionPath);
        return Ok(("solution_path", solutionPath), ("plc", plcName));
    }

    public static Result AddPlcProject(ITcSession session, string slnPath, string plcName, string projectType)
    {
        if (string.IsNullOrEmpty(plcName))
        {
            throw new ArgumentException("PlcName required.");
        }

        if (projectType != "standard")
        {
            throw new InvalidOperationException($"ProjectType '{projectType}' not supported (only 'standard').");
        }

        var template = ResolveTemplatePath();
        session.UseSolution(slnPath ?? "");
        var solutionPath = string.IsNullOrEmpty(slnPath) ? session.SolutionPath : slnPath;

        // Guard against a PlcName collision against every existing TwinCAT project.
        foreach (var sm in session.GetSysManagers())
        {
            var tipc = sm.LookupTreeItem("TIPC");
            for (var i = 1; i <= tipc.ChildCount; i++)
            {
                if (tipc.Child(i).Name == plcName)
                {
                    throw new InvalidOperationException($"PLC project '{plcName}' already exists in solution.");
                }
            }
        }

        // Each PLC lives in its own TwinCAT project ("_Tc" suffix avoids a name collision with the
        // PLC, which crashes XAE on save), placed in its own subdirectory at sln level.
        var slnDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var tcProjectName = $"{plcName}_Tc";
        session.AddProjectFromTemplate(template, Path.Combine(slnDir, tcProjectName), tcProjectName);

        var newTipc = FindEmptyTipc(session);
        newTipc.CreateChild(plcName, 0, null, "Standard PLC Template.plcproj");

        session.SaveSolutionAs(solutionPath);
        return Ok(("solution_path", solutionPath), ("plc", plcName), ("project_type", projectType));
    }

    // --- library / scaffolding helpers ---------------------------------------

    private static ITcTreeItem ReferencesNode(ITcSysManager sm, string plc)
        => sm.LookupTreeItem($"TIPC^{plc}^{plc} Project^References");

    /// <summary>
    /// Splice a parameter override block into the .plcproj and register it with the guard. Close
    /// before the file edit so the next File.SaveAll can't regenerate the .plcproj from an
    /// in-memory tree that doesn't know about the injected overrides; reopen re-hydrates it. The
    /// guard re-checks the block after every later write verb (see <see cref="ParameterGuard"/>).
    /// </summary>
    private static void SpliceParameters(
        ITcSession session, string plcProjPath, string elementName, string referenceName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        var solutionPath = session.SolutionPath;
        session.CloseSolution();
        PlcProjXml.SetReferenceParameters(plcProjPath, elementName, referenceName, parameters);
        session.UseSolution(solutionPath);
        ParameterGuard.Register(plcProjPath, elementName, referenceName, parameters);
    }

    private static string? TryFindPlcProj(string? solutionPath, string plc)
    {
        try
        {
            return PlcProjXml.Find(Path.GetDirectoryName(solutionPath), plc);
        }
#pragma warning disable CA1031 // No file yet (or unknown dir) just means "skip the on-disk probe".
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>Read EffectiveVersion off the References child matching name + distributor.</summary>
    private static string? ResolveEffectiveVersion(ITcTreeItem references, string libraryName, string distributor)
    {
        for (var i = 1; i <= references.ChildCount; i++)
        {
            var child = references.Child(i);
            if (child.Name != libraryName)
            {
                continue;
            }

            XmlDocument doc;
            try
            {
                doc = new XmlDocument();
                doc.LoadXml(child.ProduceXml(0));
            }
#pragma warning disable CA1031 // A reference that won't serialise can't be the match; skip it.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            var library = doc.SelectSingleNode("//Library");
            if (library is null)
            {
                continue;
            }

            if ((library.SelectSingleNode("Distributor")?.InnerText ?? "") != distributor)
            {
                continue;
            }

            var effective = library.SelectSingleNode("EffectiveVersion")?.InnerText;
            if (!string.IsNullOrEmpty(effective))
            {
                return effective;
            }
        }

        return null;
    }

    /// <summary>Set ProjectInfo Title/Company/Version on a PLC project's ProduceXml output.</summary>
    private static string WithProjectInfo(string projectXml, string title, string company, string version)
    {
        var doc = new XmlDocument();
        doc.LoadXml(projectXml);
        var info = doc.SelectSingleNode("//ProjectInfo")
            ?? throw new InvalidOperationException("ProjectInfo node not found in PLC project XML.");
        SetChildText(info, "Title", title);
        SetChildText(info, "Company", company);
        SetChildText(info, "Version", version);
        return doc.OuterXml;

        static void SetChildText(XmlNode parent, string child, string value)
        {
            var node = parent.SelectSingleNode(child)
                ?? throw new InvalidOperationException($"ProjectInfo/{child} node not found.");
            node.InnerText = value;
        }
    }

    private static bool IsColdStartResolverError(string message)
        => message.Contains("PlaceholderReference", StringComparison.OrdinalIgnoreCase)
            && message.Contains("EffectiveResolution", StringComparison.OrdinalIgnoreCase);

    /// <summary>The standard 4026 project template, overridable via the TC_PROJECT_TEMPLATE env var.</summary>
    private static string ResolveTemplatePath()
    {
        var template = Environment.GetEnvironmentVariable("TC_PROJECT_TEMPLATE");
        if (string.IsNullOrEmpty(template))
        {
            template = @"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PrjTemplate\TwinCAT Project.tsproj";
        }

        if (!File.Exists(template))
        {
            throw new InvalidOperationException(
                $"Project template not found: {template}. Set TC_PROJECT_TEMPLATE to the .tsproj path.");
        }

        return template;
    }

    /// <summary>
    /// Find the freshly-added TwinCAT project's TIPC (the one with no PLC yet). AddFromTemplate
    /// returns before XAE has finished exposing the new project, so poll GetSysManagers briefly.
    /// </summary>
    private static ITcTreeItem FindEmptyTipc(ITcSession session, int maxAttempts = 20, int delayMs = 250)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var sm in session.GetSysManagers())
            {
                ITcTreeItem tipc;
                try
                {
                    tipc = sm.LookupTreeItem("TIPC");
                }
#pragma warning disable CA1031 // A project still loading can't expose TIPC yet; keep polling.
                catch (Exception)
                {
                    continue;
                }
#pragma warning restore CA1031

                if (tipc.ChildCount == 0)
                {
                    return tipc;
                }
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        throw new InvalidOperationException(
            "Could not locate the new TwinCAT project's empty TIPC after AddFromTemplate.");
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

    /// <summary>The POU's declaration-bearing item: the POU itself, or a named method/action under it.</summary>
    private static ITcTreeItem LocateDeclItem(ITcSysManager sm, string plc, string pouName, string? itemName)
    {
        var pou = LocatePouUnderProject(sm, plc, pouName);
        if (string.IsNullOrEmpty(itemName) || itemName == pouName)
        {
            return pou;
        }

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

    /// <summary>
    /// Delete a tree item by resolving its parent from a captured PathName and calling DeleteChild.
    /// Takes the path as a string (captured before any navigation) rather than a live item handle:
    /// TwinCAT AI invalidates a tree-item handle once you navigate elsewhere (e.g. the parent
    /// LookupTreeItem), so reading Name off the original handle afterwards throws "deleted or
    /// invalidated by an earlier operation".
    /// </summary>
    private static string Remove(ITcSysManager sm, string pathName)
    {
        var segments = pathName.Split('^');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException($"Cannot resolve parent of '{pathName}'.");
        }

        var parentPath = string.Join('^', segments[..^1]);
        sm.LookupTreeItem(parentPath).DeleteChild(segments[^1]);
        return parentPath;
    }

    /// <summary>
    /// Delete a property's Get/Set accessors (best-effort) then the property body, re-resolving
    /// handles by path so no stale handle is reused across tree navigation.
    /// </summary>
    private static IReadOnlyList<string> RemovePropertyNode(
        ITcSysManager sm, string pouPath, string propertyPath, string propertyName)
    {
        var removed = new List<string>();
        var property = sm.LookupTreeItem(propertyPath);
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

        sm.LookupTreeItem(pouPath).DeleteChild(propertyName);
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
