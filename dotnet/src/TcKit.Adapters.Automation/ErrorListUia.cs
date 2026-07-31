using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Read the IDE Error List via UI Automation, for editions where EnvDTE's ToolWindows.ErrorList is
/// null (TcXaeShell Express). The rendered Error List is still a live WPF grid that UI Automation
/// reads regardless of edition: find the TcXaeShell window for the solution, select the Error List
/// tab (WPF virtualises away unselected tabs), filter runtime message rows out via the list's own
/// severity toggles (ADSLOGSTR output floods the list on a logging target), then walk the realised
/// ListItems page by page — each carries its Code / Description / Project / File / Line as
/// descendant Text elements. GridPattern.GetItem is NOT used: after a refilter it hands back
/// recycled containers with stale text.
///
/// Severity caveat: the Express Error List does not expose per-row severity to UI Automation, so
/// severity is inferred: a row with a compiler code (e.g. C0046) is a compile diagnostic — an
/// error when CheckAllObjects failed, a warning when it passed — and every code-less row is an
/// info. Returns null when the GUI can't be reached, so the caller keeps its honest-message
/// fallback. C# port of the bridge harness's Read-TcErrorListUia, reworked per the above; ADR-0014.
/// </summary>
internal static class ErrorListUia
{
    private const int MaxPages = 80;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

#pragma warning disable CA1031 // A GUI scrape is best-effort end to end: any UIA hiccup means "no rows", never a crash.
    public static IReadOnlyList<ComErrorItem>? Read(string solutionPath, bool compileSucceeded)
    {
        try
        {
            return ReadCore(solutionPath, compileSucceeded);
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031

    private static IReadOnlyList<ComErrorItem>? ReadCore(string solutionPath, bool compileSucceeded)
    {
        var root = FindShellWindow(solutionPath);
        if (root is null)
        {
            return null;
        }

        SelectErrorListTab(root);

        var messagesWereOn = SetSeverityToggle(root, "Messages", on: false);
        SetSeverityToggle(root, "Errors", on: true);
        SetSeverityToggle(root, "Warnings", on: true);
        try
        {
            return ReadItems(root, compileSucceeded);
        }
        finally
        {
            if (messagesWereOn == true)
            {
                SetSeverityToggle(root, "Messages", on: true);
            }
        }
    }

    private static IReadOnlyList<ComErrorItem>? ReadItems(AutomationElement root, bool compileSucceeded)
    {
        var grid = FindResultsGrid(root);
        if (grid is null)
        {
            return null;
        }

        // Walk the virtualised list page by page: scroll to the top, harvest the realised items,
        // page down, repeat until the scrollbar bottoms out (or there is nothing to scroll).
        var scroll = TryGetScrollPattern(grid);
        ScrollToTop(scroll);

        var rows = new List<ComErrorItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxPages; page++)
        {
            foreach (var item in RealisedItems(grid))
            {
                var row = ParseItem(item, compileSucceeded);
                if (row is not null && seen.Add($"{row.File}|{row.Line}|{row.Description}"))
                {
                    rows.Add(row);
                }
            }

            if (scroll is null || !CanScrollDown(scroll))
            {
                break;
            }

            scroll.ScrollVertical(ScrollAmount.LargeIncrement);
            Thread.Sleep(120); // let WPF realise the next page
        }

        return rows;
    }

    /// <summary>
    /// Parse one realised row. Its descendant Text elements arrive in column order —
    /// [Code, Description, Project, File, Line] for a compile diagnostic, or a single
    /// description-only text for a TwinCAT message row.
    /// </summary>
    private static ComErrorItem? ParseItem(AutomationElement item, bool compileSucceeded)
    {
        var texts = new List<string>();
        foreach (AutomationElement text in item.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
        {
            texts.Add(text.Current.Name ?? "");
        }

        if (texts.Count == 0)
        {
            return null; // unrealised leftover
        }

        var isCoded = LooksLikeCode(texts[0]);
        if (isCoded && texts.Count >= 2)
        {
            var code = texts[0];
            var description = texts[1];
            var project = texts.Count > 2 ? texts[2] : "";
            var file = texts.Count > 3 ? texts[3] : "";
            var line = 0;
            if (texts.Count > 4)
            {
                _ = int.TryParse(
                    new string(texts[4].Where(char.IsDigit).ToArray()),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out line);
            }

            // Level per the severity inference; the builder lifts "C0046: ..." into its code field.
            var level = compileSucceeded ? 2 : 1;
            return new ComErrorItem(file, line, $"{code}: {description}", level, project);
        }

        // Message row (or a filtered leftover): a single free-text cell, surfaced as info.
        var message = string.Join(" | ", texts.Where(t => t.Length > 0));
        return message.Length == 0 ? null : new ComErrorItem("", 0, message, 3, "");
    }

    /// <summary>"C0046"-style compiler code: a letter followed by 3+ digits.</summary>
    private static bool LooksLikeCode(string value)
        => value.Length >= 4 && char.IsLetter(value[0]) && value.Skip(1).All(char.IsDigit);

    /// <summary>Find the TcXaeShell main window for this solution ("&lt;stem&gt; - TcXaeShell"),
    /// falling back to a lone instance; restore it so WPF realises virtualised rows.</summary>
    private static AutomationElement? FindShellWindow(string solutionPath)
    {
        var stem = Path.GetFileNameWithoutExtension(solutionPath);
        var candidates = Process.GetProcessesByName("TcXaeShell")
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var process = candidates.FirstOrDefault(p =>
                p.MainWindowTitle.Contains(stem, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);
        if (process is null)
        {
            return null;
        }

        ShowWindow(process.MainWindowHandle, SwRestore);
        return AutomationElement.FromHandle(process.MainWindowHandle);
    }

    /// <summary>The grid only exists in the UIA tree while its tool-window tab is selected.</summary>
#pragma warning disable CA1031 // Tab selection is best-effort; the grid poll below is the real gate.
    private static void SelectErrorListTab(AutomationElement root)
    {
        try
        {
            var tabCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.NameProperty, "Error List"));
            var tab = root.FindFirst(TreeScope.Descendants, tabCondition);
            if (tab?.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern selection
                && !selection.Current.IsSelected)
            {
                selection.Select();
            }
        }
        catch (Exception)
        {
            // The grid poll decides whether the list is reachable.
        }
    }
#pragma warning restore CA1031

    /// <summary>
    /// Flip one of the Error List's severity filter buttons ("106 Errors" / "5 Warnings" /
    /// "699 Messages" — toggle buttons whose Name ends with the severity word). Returns the
    /// previous state, or null when the button wasn't found (best-effort: the read proceeds
    /// unfiltered).
    /// </summary>
#pragma warning disable CA1031 // Filter toggling is best-effort; an unfiltered read still works on quiet targets.
    private static bool? SetSeverityToggle(AutomationElement root, string severityWord, bool on)
    {
        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var name = (button.Current.Name ?? "").Trim();
                if (!name.EndsWith(severityWord, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (button.GetCurrentPattern(TogglePattern.Pattern) is not TogglePattern toggle)
                {
                    continue;
                }

                var wasOn = toggle.Current.ToggleState == ToggleState.On;
                if (wasOn != on)
                {
                    toggle.Toggle();
                    Thread.Sleep(250); // give the virtualised grid a beat to refilter
                }

                return wasOn;
            }
        }
        catch (Exception)
        {
            // Fall through to the unfiltered read.
        }

        return null;
    }
#pragma warning restore CA1031

    /// <summary>Poll for the Error List grid: AutomationId 'Tracking List View', falling back to a
    /// ListView named 'Results' (it realises a beat after the tab is selected).</summary>
    private static AutomationElement? FindResultsGrid(AutomationElement root)
    {
        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, "Tracking List View");
        var byName = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Results"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "ListView"));

        for (var attempt = 0; attempt < 15; attempt++)
        {
            var grid = root.FindFirst(TreeScope.Descendants, byId)
                ?? root.FindFirst(TreeScope.Descendants, byName);
            if (grid is not null)
            {
                return grid;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private static IEnumerable<AutomationElement> RealisedItems(AutomationElement grid)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
        foreach (AutomationElement item in grid.FindAll(TreeScope.Children, condition))
        {
            yield return item;
        }
    }

#pragma warning disable CA1031 // Scrolling is best-effort; without it we still return the visible page.
    private static ScrollPattern? TryGetScrollPattern(AutomationElement grid)
    {
        try
        {
            return grid.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ScrollToTop(ScrollPattern? scroll)
    {
        try
        {
            if (scroll is not null && scroll.Current.VerticallyScrollable)
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                Thread.Sleep(150);
            }
        }
        catch (Exception)
        {
            // Start from wherever the list is.
        }
    }

    private static bool CanScrollDown(ScrollPattern scroll)
    {
        try
        {
            return scroll.Current.VerticallyScrollable && scroll.Current.VerticalScrollPercent < 100;
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1031
}
