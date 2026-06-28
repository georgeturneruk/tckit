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

    public void CheckAllObjects() => ComRetry.Invoke(() => { _item.CheckAllObjects(); });
}

/// <summary>COM-backed <see cref="ITcSysManager"/> over a late-bound ITcSysManager.</summary>
internal sealed class ComTcSysManager(dynamic sysManager) : ITcSysManager
{
    private readonly dynamic _sm = sysManager;

    public ITcTreeItem LookupTreeItem(string path)
        => new ComTcTreeItem(ComRetry.Invoke<object>(() => _sm.LookupTreeItem(path)));
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
        => ProbeSysManagers().Select(sm => (ITcSysManager)new ComTcSysManager(sm)).ToList();

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

        dynamic sm = managers[0];
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
