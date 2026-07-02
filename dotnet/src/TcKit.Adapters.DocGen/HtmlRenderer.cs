using System.Text;

namespace TcKit.Adapters.DocGen;

/// <summary>
/// Renders the HTML documentation pages. Ports the Python Jinja templates (<c>object.html</c>,
/// <c>index.html</c>, <c>hierarchy.html</c>, <c>solution_index.html</c>) and the search-index builder.
/// </summary>
internal static class HtmlRenderer
{
    private static string E(string? s) => RenderHelpers.Escape(s);

    // (obj_type, sidebar/index short label) and the section headings, in display order.
    private static readonly (string Key, string Label)[] s_navTypes =
    [
        ("function_block", "FB"), ("program", "PRG"), ("function", "FN"),
        ("interface", "ITF"), ("gvl", "GVL"), ("struct", "ST"), ("enum", "ENUM"),
    ];

    private static readonly (string Key, string Label)[] s_indexSections =
    [
        ("function_block", "Function Blocks"), ("program", "Programs"), ("function", "Functions"),
        ("interface", "Interfaces"), ("gvl", "Global Variable Lists"), ("struct", "Structures"),
        ("enum", "Enumerations"),
    ];

    private static readonly (string Key, string Label)[] s_countLabels =
    [
        ("function_block", "function block"), ("program", "program"), ("function", "function"),
        ("interface", "interface"), ("gvl", "GVL"), ("struct", "struct"), ("enum", "enum"),
    ];

    /// <summary>Wrap page content in the shared layout, filling in title, project name, and the typed nav.</summary>
    internal static string Page(PlcDoc plc, string titleText, string? currentName, string content)
        => HtmlLayout.Template
            .Replace(HtmlLayout.TitleToken, E(titleText), StringComparison.Ordinal)
            .Replace(HtmlLayout.ProjectNameToken, E(plc.Name), StringComparison.Ordinal)
            .Replace(HtmlLayout.NavToken, Nav(plc, currentName), StringComparison.Ordinal)
            .Replace(HtmlLayout.ContentToken, content, StringComparison.Ordinal);

    private static string Nav(PlcDoc plc, string? currentName)
    {
        var sb = new StringBuilder();
        foreach (var (key, label) in s_navTypes)
        {
            var objs = plc.Objects.Where(o => o.ObjType == key).ToList();
            if (objs.Count == 0)
            {
                continue;
            }

            sb.Append("    <div class=\"nav-section\">").Append(label).Append("</div>\n");
            foreach (var obj in objs)
            {
                var active = currentName == obj.Name ? " class=\"active\"" : "";
                sb.Append("    <a href=\"").Append(E(obj.Name)).Append(".html\"").Append(active).Append('>')
                    .Append(E(obj.Name)).Append("</a>\n");
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    // -- Per-PLC index --------------------------------------------------------

    internal static string PlcIndex(PlcDoc plc)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(E(plc.Name)).Append("</h1>\n");

        var count = plc.Objects.Count;
        sb.Append("<p class=\"description\">\n  ").Append(count).Append(" object").Append(count != 1 ? "s" : "")
            .Append("\n  &mdash;\n  ");
        foreach (var (key, label) in s_countLabels)
        {
            var n = plc.Objects.Count(o => o.ObjType == key);
            if (n > 0)
            {
                sb.Append(n).Append(' ').Append(label).Append(n != 1 ? "s" : "").Append("&ensp;\n  ");
            }
        }

        sb.Append("\n</p>\n\n<hr>\n");

        foreach (var (key, label) in s_indexSections)
        {
            var objs = plc.Objects.Where(o => o.ObjType == key).ToList();
            if (objs.Count == 0)
            {
                continue;
            }

            sb.Append("<h2>").Append(label).Append("</h2>\n<table>\n<thead><tr><th>Name</th><th>Description</th></tr></thead>\n<tbody>\n");
            foreach (var obj in objs)
            {
                var desc = obj.Comment.Description.Length > 0 ? E(RenderHelpers.Truncate(obj.Comment.Description, 120)) : "";
                sb.Append("<tr><td>").Append(IndexBadge(obj.ObjType)).Append(" <a href=\"").Append(E(obj.Name))
                    .Append(".html\">").Append(E(obj.Name)).Append("</a></td><td class=\"description\">")
                    .Append(desc).Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        return Page(plc, plc.Name, null, sb.ToString());
    }

    // -- Object page ----------------------------------------------------------

    internal static string ObjectPage(ObjectDoc obj, PlcDoc plc, IReadOnlySet<string> known)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>\n  ").Append(E(obj.Name)).Append("&ensp;").Append(ObjectBadge(obj.ObjType));
        if (obj.Visibility.Length > 0)
        {
            sb.Append(VisibilityBadge(obj.Visibility));
        }

        if (obj.IsAbstract)
        {
            sb.Append("<span class=\"badge badge-prot\">ABSTRACT</span>");
        }

        if (obj.IsFinal)
        {
            sb.Append("<span class=\"badge badge-prot\">FINAL</span>");
        }

        sb.Append("\n</h1>\n");

        if (obj.Extends.Length > 0 || obj.Implements.Count > 0)
        {
            sb.Append("<p style=\"color: var(--text-dim); font-size: 0.85rem; margin-bottom: 0.75rem;\">\n");
            if (obj.Extends.Length > 0)
            {
                sb.Append("  Extends <a href=\"").Append(E(obj.Extends)).Append(".html\">").Append(E(obj.Extends)).Append("</a>");
            }

            if (obj.Extends.Length > 0 && obj.Implements.Count > 0)
            {
                sb.Append("&ensp;&mdash;&ensp;");
            }

            if (obj.Implements.Count > 0)
            {
                sb.Append("Implements ");
                for (var i = 0; i < obj.Implements.Count; i++)
                {
                    var iface = obj.Implements[i];
                    sb.Append("<a href=\"").Append(E(iface)).Append(".html\">").Append(E(iface)).Append("</a>");
                    if (i < obj.Implements.Count - 1)
                    {
                        sb.Append(", ");
                    }
                }
            }

            sb.Append("\n</p>\n");
        }

        if (obj.Comment.Description.Length > 0)
        {
            sb.Append("<p class=\"description\">").Append(E(obj.Comment.Description)).Append("</p>\n");
        }

        if (obj.Comment.Remarks.Length > 0)
        {
            sb.Append("<p class=\"description\"><strong>Remarks:</strong> ").Append(E(obj.Comment.Remarks)).Append("</p>\n");
        }

        sb.Append("<hr>\n");

        sb.Append(VarTable(obj.Inputs, "Inputs", known));
        sb.Append(VarTable(obj.Inout, "In/Out", known));
        sb.Append(VarTable(obj.Outputs, "Outputs", known));
        sb.Append(obj.ObjType switch
        {
            "enum" => EnumTable(obj.Variables, "Members"),
            "struct" => VarTable(obj.Variables, "Fields", known),
            "gvl" => VarTable(obj.Variables, "Globals", known),
            _ => VarTable(obj.Variables, "Variables", known),
        });

        if (obj.Methods.Count > 0)
        {
            sb.Append("<h2>Methods</h2>\n");
            foreach (var method in obj.Methods)
            {
                sb.Append("<div class=\"item-card\">\n  <div class=\"item-card-header\">\n    <span class=\"item-signature\">\n      <span class=\"item-name\">")
                    .Append(E(method.Name)).Append("</span>");
                if (method.ReturnType.Length > 0)
                {
                    sb.Append("<span class=\"item-return-type\"> : ").Append(RenderHelpers.LinkType(method.ReturnType, known)).Append("</span>");
                }

                sb.Append("\n    </span>\n    <span class=\"item-badges\">\n");
                if (method.Visibility.Length > 0)
                {
                    sb.Append("      ").Append(VisibilityBadge(method.Visibility)).Append('\n');
                }

                if (method.IsAbstract)
                {
                    sb.Append("      <span class=\"badge badge-prot\">ABSTRACT</span>\n");
                }

                if (method.IsFinal)
                {
                    sb.Append("      <span class=\"badge badge-prot\">FINAL</span>\n");
                }

                sb.Append("    </span>\n  </div>\n  <div class=\"item-card-body\">\n");
                if (method.Comment.Description.Length > 0)
                {
                    sb.Append("    <p class=\"description\">").Append(E(method.Comment.Description)).Append("</p>\n");
                }

                sb.Append(VarTable(method.Inputs, "Parameters", known));
                sb.Append(VarTable(method.Inout, "In/Out", known));
                sb.Append(VarTable(method.Outputs, "Outputs", known));

                if (method.Comment.Returns.Length > 0)
                {
                    sb.Append("    <p><strong>Returns:</strong> ").Append(E(method.Comment.Returns)).Append("</p>\n");
                }

                if (method.Body.Length > 0)
                {
                    sb.Append("    <details class=\"impl-details\">\n      <summary>Implementation</summary>\n      <pre><code>")
                        .Append(E(method.Body)).Append("</code></pre>\n    </details>\n");
                }

                sb.Append("  </div>\n</div>\n");
            }
        }

        if (obj.Properties.Count > 0)
        {
            sb.Append("<h2>Properties</h2>\n");
            foreach (var prop in obj.Properties)
            {
                sb.Append("<div class=\"item-card\">\n  <div class=\"item-card-header\">\n    <span class=\"item-signature\">\n      <span class=\"item-name\">")
                    .Append(E(prop.Name)).Append("</span>");
                if (prop.ReturnType.Length > 0)
                {
                    sb.Append("<span class=\"item-return-type\"> : ").Append(RenderHelpers.LinkType(prop.ReturnType, known)).Append("</span>");
                }

                sb.Append("\n    </span>\n    <span class=\"item-badges\">\n");
                if (prop.Visibility.Length > 0)
                {
                    sb.Append("      ").Append(VisibilityBadge(prop.Visibility)).Append('\n');
                }

                if (prop.HasGet)
                {
                    sb.Append("      <span class=\"badge badge-get\">GET</span>\n");
                }

                if (prop.HasSet)
                {
                    sb.Append("      <span class=\"badge badge-set\">SET</span>\n");
                }

                sb.Append("    </span>\n  </div>\n");
                if (prop.Comment.Description.Length > 0)
                {
                    sb.Append("  <div class=\"item-card-body\">\n    <p class=\"description\">")
                        .Append(E(prop.Comment.Description)).Append("</p>\n  </div>\n");
                }

                sb.Append("</div>\n");
            }
        }

        if (obj.Actions.Count > 0)
        {
            sb.Append("<h2>Actions</h2>\n<ul>\n");
            foreach (var action in obj.Actions)
            {
                sb.Append("  <li>").Append(E(action)).Append("</li>\n");
            }

            sb.Append("</ul>\n");
        }

        if (obj.UsedBy.Count > 0)
        {
            sb.Append("<h2>Used by</h2>\n<ul>\n");
            foreach (var name in obj.UsedBy.OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.Append("  <li><a href=\"").Append(E(name)).Append(".html\">").Append(E(name)).Append("</a></li>\n");
            }

            sb.Append("</ul>\n");
        }

        sb.Append("<details class=\"impl-details declaration-details\">\n  <summary>Declaration source</summary>\n  <pre><code>")
            .Append(E(obj.Declaration)).Append("</code></pre>\n</details>\n");

        return Page(plc, obj.Name, obj.Name, sb.ToString());
    }

    private static string VarTable(IReadOnlyList<VariableDoc> vars, string title, IReadOnlySet<string> known)
    {
        if (vars.Count == 0)
        {
            return "";
        }

        var hasDefaults = vars.Any(v => v.DefaultValue.Length > 0);
        var sb = new StringBuilder();
        sb.Append("<h3>").Append(title).Append("</h3>\n<table>\n<thead><tr><th>Name</th><th>Type</th>");
        if (hasDefaults)
        {
            sb.Append("<th>Default</th>");
        }

        sb.Append("<th>Description</th></tr></thead>\n<tbody>\n");
        foreach (var v in vars)
        {
            sb.Append("<tr><td><code>").Append(E(v.Name)).Append("</code></td><td>")
                .Append(RenderHelpers.LinkType(v.VarType, known)).Append("</td>");
            if (hasDefaults)
            {
                sb.Append("<td>");
                if (v.DefaultValue.Length > 0)
                {
                    sb.Append("<code class=\"default-value\">").Append(E(v.DefaultValue)).Append("</code>");
                }

                sb.Append("</td>");
            }

            sb.Append("<td class=\"description\">").Append(E(v.Comment)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
        return sb.ToString();
    }

    private static string EnumTable(IReadOnlyList<VariableDoc> members, string title)
    {
        if (members.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("<h3>").Append(title).Append("</h3>\n<table>\n<thead><tr><th>Name</th><th>Value</th><th>Description</th></tr></thead>\n<tbody>\n");
        foreach (var m in members)
        {
            sb.Append("<tr><td><code>").Append(E(m.Name)).Append("</code></td><td><code>").Append(E(m.VarType))
                .Append("</code></td><td class=\"description\">").Append(E(m.Comment)).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
        return sb.ToString();
    }

    // -- Hierarchy page -------------------------------------------------------

    internal static string Hierarchy(PlcDoc plc, IReadOnlySet<string> known)
    {
        var byBase = new SortedDictionary<string, List<ObjectDoc>>(StringComparer.OrdinalIgnoreCase);
        var byIface = new SortedDictionary<string, List<ObjectDoc>>(StringComparer.OrdinalIgnoreCase);
        var standalone = new List<ObjectDoc>();

        foreach (var obj in plc.Objects)
        {
            if (obj.Extends.Length > 0)
            {
                (byBase.TryGetValue(obj.Extends, out var list) ? list : byBase[obj.Extends] = []).Add(obj);
            }

            foreach (var iface in obj.Implements)
            {
                (byIface.TryGetValue(iface, out var list) ? list : byIface[iface] = []).Add(obj);
            }

            if (obj.Extends.Length == 0 && obj.Implements.Count == 0)
            {
                standalone.Add(obj);
            }
        }

        var sb = new StringBuilder();
        sb.Append("<h1>Type Hierarchy</h1>\n<p class=\"description\">Inheritance and interface implementation relationships across the project.</p>\n\n<hr>\n");

        if (byBase.Count > 0)
        {
            sb.Append("<h2>Inheritance</h2>\n<table>\n<thead><tr><th>Base class</th><th>Derived classes</th></tr></thead>\n<tbody>\n");
            foreach (var (baseName, children) in byBase)
            {
                sb.Append("<tr><td>").Append(NameOrDim(baseName, known)).Append("</td><td>");
                AppendBadgedLinks(sb, children);
                sb.Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        if (byIface.Count > 0)
        {
            sb.Append("<h2>Interface Implementations</h2>\n<table>\n<thead><tr><th>Interface</th><th>Implementors</th></tr></thead>\n<tbody>\n");
            foreach (var (ifaceName, implementors) in byIface)
            {
                sb.Append("<tr><td>").Append(NameOrDim(ifaceName, known)).Append("</td><td>");
                AppendBadgedLinks(sb, implementors);
                sb.Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        if (standalone.Count > 0)
        {
            sb.Append("<h2>Standalone</h2>\n<p class=\"description\">Objects with no inheritance or interface relationships.</p>\n<table>\n<thead><tr><th>Name</th><th>Type</th><th>Description</th></tr></thead>\n<tbody>\n");
            foreach (var obj in standalone)
            {
                sb.Append("<tr><td>").Append(IndexBadge(obj.ObjType)).Append(" <a href=\"").Append(E(obj.Name))
                    .Append(".html\">").Append(E(obj.Name)).Append("</a></td><td style=\"color:var(--text-dim);font-size:0.8rem\">")
                    .Append(E(RenderHelpers.Title(obj.ObjType.Replace('_', ' ')))).Append("</td><td class=\"description\">")
                    .Append(E(RenderHelpers.Truncate(obj.Comment.Description, 100))).Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        if (byBase.Count == 0 && byIface.Count == 0)
        {
            sb.Append("<p class=\"description\">No inheritance or interface relationships found in this project.</p>\n");
        }

        return Page(plc, "Hierarchy", null, sb.ToString());
    }

    private static void AppendBadgedLinks(StringBuilder sb, IReadOnlyList<ObjectDoc> objs)
    {
        for (var i = 0; i < objs.Count; i++)
        {
            sb.Append(IndexBadge(objs[i].ObjType)).Append(" <a href=\"").Append(E(objs[i].Name)).Append(".html\">")
                .Append(E(objs[i].Name)).Append("</a>");
            if (i < objs.Count - 1)
            {
                sb.Append("&ensp;");
            }
        }
    }

    private static string NameOrDim(string name, IReadOnlySet<string> known)
        => known.Contains(name)
            ? $"<a href=\"{E(name)}.html\">{E(name)}</a>"
            : $"<span style=\"color:var(--text-dim)\">{E(name)}</span>";

    // -- Solution index -------------------------------------------------------

    internal static string SolutionIndex(ProjectDoc project)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\" data-theme=\"dark\">\n<head>\n  <meta charset=\"UTF-8\">\n");
        sb.Append("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append("  <title>").Append(E(project.Name)).Append(" &mdash; TcKit Docs</title>\n");
        sb.Append("  <link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\n");
        sb.Append("  <link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>\n");
        sb.Append("  <link href=\"https://fonts.googleapis.com/css2?family=DM+Sans:wght@400;500;600&family=JetBrains+Mono:ital,wght@0,400;0,500;1,400&display=swap\" rel=\"stylesheet\">\n");
        sb.Append(SolutionIndexStyle);
        sb.Append("</head>\n<body>\n");
        sb.Append("  <h1>").Append(E(project.Name)).Append("</h1>\n");
        var plcCount = project.Plcs.Count;
        sb.Append("  <p class=\"description\">\n    Solution containing ").Append(plcCount).Append(" PLC project")
            .Append(plcCount != 1 ? "s" : "").Append(".\n  </p>\n\n");
        sb.Append("  <h2>PLC projects</h2>\n");
        if (project.Plcs.Count > 0)
        {
            sb.Append("  <table>\n    <thead><tr><th>PLC project</th><th>Objects</th></tr></thead>\n    <tbody>\n");
            foreach (var (_, plc) in project.Plcs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("      <tr>\n        <td><a href=\"").Append(E(plc.Name)).Append("/index.html\">")
                    .Append(E(plc.Name)).Append("</a></td>\n        <td>").Append(plc.Objects.Count).Append("</td>\n      </tr>\n");
            }

            sb.Append("    </tbody>\n  </table>\n");
        }
        else
        {
            sb.Append("  <p><em>No PLC projects discovered in this solution.</em></p>\n");
        }

        sb.Append("\n  <footer>\n    Built with <a href=\"https://tckit.org\" target=\"_blank\" rel=\"noopener\">TcKit</a>\n  </footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private const string SolutionIndexStyle = """
  <style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    :root {
      --bg: #0E0C0D; --bg-panel: #110C0B; --bg-code: #1a1614;
      --border: rgba(213,208,207,0.12); --accent: #DA7557;
      --accent-lt: #E8987E; --text: rgba(250,245,244,0.87);
      --text-dim: rgba(250,245,244,0.54);
      --font-body: 'JetBrains Mono', 'SF Mono', monospace;
      --font-head: 'DM Sans', system-ui, sans-serif;
    }
    html { font-size: 14px; }
    body {
      background: var(--bg); color: var(--text);
      font-family: var(--font-body); line-height: 1.7;
      padding: 2rem 2.5rem; max-width: 900px; margin: 0 auto;
    }
    h1 { font-family: var(--font-head); font-size: 1.8rem; font-weight: 600; margin-bottom: 0.5rem; }
    h2 { font-family: var(--font-head); font-size: 1.2rem; font-weight: 600; margin: 2rem 0 0.75rem; }
    a { color: var(--accent); text-decoration: none; }
    a:hover { color: var(--accent-lt); text-decoration: underline; }
    .description { color: var(--text-dim); margin-bottom: 1rem; }
    table { width: 100%; border-collapse: collapse; margin-bottom: 1rem; font-size: 0.85rem; }
    th { background: var(--bg-code); color: var(--text-dim); font-family: var(--font-head); font-weight: 600; font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.05em; padding: 0.5rem 0.75rem; text-align: left; border-bottom: 1px solid var(--border); }
    td { padding: 0.45rem 0.75rem; border-bottom: 1px solid var(--border); }
    footer { text-align: right; padding-top: 2rem; font-size: 0.72rem; color: var(--text-dim); }
    footer a { color: var(--text-dim); }
  </style>
""";

    // -- Badges ---------------------------------------------------------------

    private static string ObjectBadge(string objType) => objType switch
    {
        "function_block" => "<span class=\"badge badge-fb\">Function Block</span>",
        "program" => "<span class=\"badge badge-prg\">Program</span>",
        "function" => "<span class=\"badge badge-fn\">Function</span>",
        "interface" => "<span class=\"badge badge-itf\">Interface</span>",
        "gvl" => "<span class=\"badge badge-gvl\">GVL</span>",
        "struct" => "<span class=\"badge badge-struct\">Struct</span>",
        "enum" => "<span class=\"badge badge-enum\">Enum</span>",
        _ => "",
    };

    private static string IndexBadge(string objType) => objType switch
    {
        "function_block" => "<span class=\"badge badge-fb\">FB</span>",
        "program" => "<span class=\"badge badge-prg\">PRG</span>",
        "function" => "<span class=\"badge badge-fn\">FN</span>",
        "interface" => "<span class=\"badge badge-itf\">ITF</span>",
        "gvl" => "<span class=\"badge badge-gvl\">GVL</span>",
        "struct" => "<span class=\"badge badge-struct\">ST</span>",
        "enum" => "<span class=\"badge badge-enum\">ENUM</span>",
        _ => "",
    };

    private static string VisibilityBadge(string visibility)
    {
        var cls = visibility == "PUBLIC" ? "pub" : visibility == "PRIVATE" ? "priv" : "prot";
        return $"<span class=\"badge badge-{cls}\">{E(visibility)}</span>";
    }
}
