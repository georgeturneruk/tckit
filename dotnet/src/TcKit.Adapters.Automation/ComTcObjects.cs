using System.Runtime.InteropServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM-backed <see cref="ITcTreeItem"/> over a late-bound ITcSmTreeItem. Every call is wrapped in
/// <see cref="ComRetry"/> for the XAE-busy rejections. Assumes it runs on the STA worker thread.
/// </summary>
internal sealed class ComTcTreeItem(dynamic item) : ITcTreeItem
{
    private readonly dynamic _item = item;

    public string Name => ComRetry.Invoke(() => (string)_item.Name);

    public string PathName => ComRetry.Invoke(() => (string)_item.PathName);

    public int ItemType => ComRetry.Invoke(() => (int)_item.ItemType);

    public int ChildCount => ComRetry.Invoke(() => (int)_item.ChildCount);

    public string DeclarationText
    {
        get => ComRetry.Invoke(() => (string)_item.DeclarationText);
        set => ComRetry.Invoke(() => { _item.DeclarationText = value; });
    }

    public string ImplementationText
    {
        get => ComRetry.Invoke(() => (string)_item.ImplementationText);
        set => ComRetry.Invoke(() => { _item.ImplementationText = value; });
    }

    public ITcTreeItem Child(int index)
        => new ComTcTreeItem(ComRetry.Invoke<object>(() => _item.Child(index)));

    public ITcTreeItem CreateChild(string name, int kind, object? before, object? vInfo)
        => new ComTcTreeItem(ComRetry.Invoke<object>(() => _item.CreateChild(name, kind, before, vInfo)));

    public void DeleteChild(string name) => ComRetry.Invoke(() => { _item.DeleteChild(name); });

    public string ProduceXml(int flags) => ComRetry.Invoke(() => (string)_item.ProduceXml(flags));

    public void ConsumeXml(string xml) => ComRetry.Invoke(() => { _item.ConsumeXml(xml); });

    public void AddLibrary(string name, string version, string distributor)
        => ComRetry.Invoke(() => { _item.AddLibrary(name, version, distributor); });

    public void AddPlaceholder(string name, string defaultLibrary, string version, string distributor)
        => ComRetry.Invoke(() => { _item.AddPlaceholder(name, defaultLibrary, version, distributor); });

    public void RemoveReference(string name)
        => ComRetry.Invoke(() => { _item.RemoveReference(name); });

    public void RemoveReference(string name, string version, string distributor)
        => ComRetry.Invoke(() => { _item.RemoveReference(name, version, distributor); });

    public void SaveAsLibrary(string outputPath, bool install)
        => ComRetry.Invoke(() => { _item.SaveAsLibrary(outputPath, install); });

    public bool CheckAllObjects() => ComRetry.Invoke(() => (bool)_item.CheckAllObjects());

    public bool BootProjectAutostart
    {
        get => ComRetry.Invoke(() => (bool)_item.BootProjectAutostart);
        set => ComRetry.Invoke(() => { _item.BootProjectAutostart = value; });
    }

    public void GenerateBootProject(bool activate)
        => ComRetry.Invoke(() => { _item.GenerateBootProject(activate); });
}

/// <summary>COM-backed <see cref="ITcSysManager"/> over a late-bound ITcSysManager.</summary>
internal sealed class ComTcSysManager(string projectName, dynamic sysManager, dynamic dteProject) : ITcSysManager
{
    private readonly dynamic _sm = sysManager;
    private readonly dynamic _project = dteProject;

    public string ProjectName { get; } = projectName;

    public ITcTreeItem LookupTreeItem(string path)
        => new ComTcTreeItem(ComRetry.Invoke<object>(() => _sm.LookupTreeItem(path)));

    public void SetTargetNetId(string amsNetId) => ComRetry.Invoke(() => { _sm.SetTargetNetId(amsNetId); });

    public void ActivateConfiguration() => ComRetry.Invoke(() => { _sm.ActivateConfiguration(); });

    // EnvDTE Project.Save() flushes this project's .tsproj (incl. the System/I-O structure) to disk.
    public void Save() => ComRetry.Invoke(() => { _project.Save(); });
}

/// <summary>
/// COM-backed <see cref="ITcSession"/> over a running TcXaeShell DTE. Attaches via the
/// <c>GetActiveObject</c> P/Invoke (Marshal.GetActiveObject is gone from net8) and ports the
/// bridge harness's solution-open, lazy-load wait, and sysmanager probing. Created and used only
/// on the STA worker thread; does not release the live DTE (it is the user's running instance).
/// </summary>
internal sealed class ComTcSession : ITcSession
{
    private static string ProgId
        => $"TcXaeShell.DTE.{Environment.GetEnvironmentVariable("COM_VERSION") ?? "17.0"}";

    private readonly dynamic _dte;

    public ComTcSession() => _dte = Attach();

    public string SolutionPath => ComRetry.Invoke(() => (string)_dte.Solution.FullName);

    public void UseSolution(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            ComRetry.Invoke(() => _dte.Solution.Open(path));
            WaitPlcProjectsLoaded();
            return;
        }

        string current = ComRetry.Invoke(() => (string)_dte.Solution.FullName);
        if (string.IsNullOrEmpty(current))
        {
            throw new InvalidOperationException(
                "No solution is open in TcXaeShell. Call OpenProject (or open it in XAE) first.");
        }

        WaitPlcProjectsLoaded();
    }

    public IReadOnlyList<ITcSysManager> GetSysManagers()
        => ProbeSysManagers()
            .Select(p => (ITcSysManager)new ComTcSysManager((string)p.Name, p.Object, p))
            .ToList();

    public void Save()
    {
        try
        {
            ComRetry.Invoke(() => _dte.ExecuteCommand("File.SaveAll"));
        }
        catch (COMException)
        {
            // Best-effort: a rejected SaveAll (e.g. during a build) must not fail the write.
        }
    }

    public void CreateSolution(string directory, string name)
    {
        // On a pre-loaded XAE, Solution.Create throws because something is already
        // open; close any loaded solution and retry once (mirrors New-TcProject.ps1).
        try
        {
            ComRetry.Invoke(() => _dte.Solution.Create(directory, name));
        }
        catch (COMException)
        {
            try
            {
                ComRetry.Invoke(() => _dte.Solution.Close(false));
            }
            catch (COMException)
            {
                // No solution to close; fall through to the retry.
            }

            ComRetry.Invoke(() => _dte.Solution.Create(directory, name));
        }
    }

    public void AddProjectFromTemplate(string templatePath, string destinationDir, string name)
        => ComRetry.Invoke(() => _dte.Solution.AddFromTemplate(templatePath, destinationDir, name, false));

    public void SaveSolutionAs(string path)
    {
        // SaveAs alone does not flush the full <System>/<Plc>/<Instance> structure to
        // the .tsproj; File.SaveAll does (the wizard does this under the hood). Without
        // it XAE segfaults in IVsParentProject.OpenChildren on reload. See New-TcProject.ps1.
        ComRetry.Invoke(() => _dte.Solution.SaveAs(path));
        Save();
    }

    public void CloseSolution() => ComRetry.Invoke(() => _dte.Solution.Close(false));

    public string ResolveSolutionConfiguration(string prefer)
    {
        dynamic sb = ComRetry.Invoke<object>(() => _dte.Solution.SolutionBuild);

        var activeName = TryGetActiveConfigName(sb);
        if (activeName is not null)
        {
            return activeName;
        }

        var configs = new List<dynamic>();
        dynamic all = sb.SolutionConfigurations;
        var count = (int)all.Count;
        for (var i = 1; i <= count; i++)
        {
            configs.Add(all.Item(i));
        }

        if (configs.Count == 0)
        {
            throw new InvalidOperationException(
                "No solution configuration is available to activate. Add a build configuration in XAE (Build > Configuration Manager).");
        }

        dynamic? chosen = null;
        if (configs.Count == 1)
        {
            chosen = configs[0];
        }
        else
        {
            foreach (var c in configs)
            {
                if (((string)c.Name).StartsWith(prefer, StringComparison.Ordinal))
                {
                    chosen = c;
                    break;
                }
            }
        }

        if (chosen is null)
        {
            var names = new List<string>();
            foreach (var c in configs)
            {
                names.Add((string)c.Name);
            }

            throw new InvalidOperationException(
                $"No active solution configuration is selected and none matches '{prefer}' "
                + $"(available: {string.Join(", ", names)}). Select one in XAE (Build > Configuration Manager).");
        }

        dynamic resolved = chosen;
        ComRetry.Invoke(() => { resolved.Activate(); });
        return (string)resolved.Name;
    }

    public IReadOnlyList<ComErrorItem>? ReadErrorList()
    {
        dynamic? errorList = TryGet(() => _dte.ToolWindows.ErrorList);
        if (errorList is null)
        {
            return null;
        }

        dynamic? items = TryGet(() => errorList.ErrorItems);
        if (items is null)
        {
            return [];
        }

        var count = TryGetInt(() => (int)items.Count);
        var rows = new List<ComErrorItem>();
        for (var i = 1; i <= count; i++)
        {
            var captured = i;
            dynamic? it = TryGet(() => items.Item(captured));
            if (it is null)
            {
                continue;
            }

            rows.Add(new ComErrorItem(
                TryGetStr(() => (string)it.FileName),
                TryGetInt(() => (int)it.Line),
                TryGetStr(() => (string)it.Description),
                TryGetInt(() => (int)it.ErrorLevel, 1),
                TryGetStr(() => (string)it.Project)));
        }

        return rows;
    }

    private static string? TryGetActiveConfigName(dynamic solutionBuild)
    {
        var active = TryGet(() => solutionBuild.ActiveConfiguration);
        if (active is null)
        {
            return null;
        }

        return TryGetStr(() => (string)active.Name);
    }

#pragma warning disable CA1031 // Best-effort late-bound COM reads: a missing member maps to a default, never throws.
    private static dynamic? TryGet(Func<dynamic?> get)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TryGetStr(Func<string> get, string fallback = "")
    {
        try
        {
            return get() ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static int TryGetInt(Func<int> get, int fallback = 0)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
#pragma warning restore CA1031

    public void Dispose()
    {
        // Intentionally no Marshal release: _dte is the user's running XAE instance, not ours to free.
    }

    private static dynamic Attach()
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

    private List<dynamic> ProbeSysManagers(int maxAttempts = 8, int delayMs = 250)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            dynamic projects = _dte.Solution.Projects;
            var count = (int)projects.Count;
            if (count > 0)
            {
                var found = new List<dynamic>();
                for (var i = 1; i <= count; i++)
                {
#pragma warning disable CA1031 // A non-XAE project (Drive Manager, measurement, solution folder) that
                    // doesn't behave like an ITcSysManager must be skipped, not abort the whole probe.
                    try
                    {
                        // Keep the EnvDTE project object (not just its .Object sys manager): it carries the
                        // project Name (to target one project) and Save() (to flush that .tsproj).
                        dynamic project = projects.Item(i);
                        dynamic obj = project.Object;
                        if (obj is null)
                        {
                            continue;
                        }

                        // Probe: only a TwinCAT XAE system-manager project answers LookupTreeItem("TIPC").
                        // A Drive Manager project's .Object exposes no such member and throws here (as a
                        // COMException or an DLR RuntimeBinderException) -> skip it and keep enumerating.
                        obj.LookupTreeItem("TIPC");
                        found.Add(project);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
#pragma warning restore CA1031
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

    private void WaitPlcProjectsLoaded(int maxAttempts = 12, int delayMs = 250)
    {
        if ((int)_dte.Solution.Projects.Count == 0)
        {
            return;
        }

        List<dynamic> managers;
        try
        {
            managers = ProbeSysManagers(maxAttempts: 1, delayMs: 0);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        dynamic sm = managers[0].Object;   // ProbeSysManagers now returns EnvDTE projects; .Object is the sys manager
        dynamic tipc = ComRetry.Invoke<object>(() => sm.LookupTreeItem("TIPC"));
        var plcNames = new List<string>();
        for (var i = 1; i <= (int)tipc.ChildCount; i++)
        {
            plcNames.Add((string)tipc.Child(i).Name);
        }

        foreach (var plcName in plcNames)
        {
            var projectPath = $"TIPC^{plcName}^{plcName} Project";
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    sm.LookupTreeItem(projectPath);
                    break;
                }
                catch (COMException)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }
    }
}
