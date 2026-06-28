using System.Xml;

namespace TcKit.Adapters.Automation;

/// <summary>
/// File-only (.plcproj) helpers for the library-placeholder lane. The Automation Interface exposes
/// no documented surface for placeholder *parameter overrides* (the IDE's "Library Parameters"
/// dialog has no programmatic counterpart on ITcPlcLibraryManager / ITcPlcPlaceholderRef, and the
/// placeholder tree item's ConsumeXml schema is undocumented), so the MSBuild XML that XAE itself
/// writes on disk is the only reliable target. This is the one documented exception to the
/// never-edit-XML-directly rule. COM-free, so it is unit-tested against temp files. Mirrors
/// Find-TcPlcProjFile / Test-TcPlcProjHasPlaceholder / Set-TcPlcProjPlaceholderParameters in
/// bridge/harness/_TcDte.psm1.
/// </summary>
internal static class PlcProjXml
{
    /// <summary>
    /// Resolve the consumer PLC's .plcproj by name, searching recursively from the solution dir.
    /// Throws on zero or ambiguous matches (mirrors Find-TcPlcProjFile).
    /// </summary>
    public static string Find(string? solutionDir, string plcName)
    {
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
        {
            throw new InvalidOperationException($"Solution directory not found: {solutionDir}");
        }

        var matches = Directory.GetFiles(solutionDir, $"{plcName}.plcproj", SearchOption.AllDirectories);
        return matches.Length switch
        {
            0 => throw new InvalidOperationException($"No .plcproj file found for PLC '{plcName}' under {solutionDir}."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple .plcproj files match PLC name '{plcName}' under {solutionDir}: {string.Join(", ", matches)}"),
        };
    }

    /// <summary>True when the .plcproj already declares a &lt;PlaceholderReference Include="name"&gt;.</summary>
    public static bool HasPlaceholder(string plcProjPath, string placeholderName)
    {
        if (!File.Exists(plcProjPath))
        {
            return false;
        }

        XmlDocument doc;
        try
        {
            doc = Load(plcProjPath);
        }
#pragma warning disable CA1031 // A malformed file means "let the COM path surface the real error".
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031

        var (nsMgr, prefix) = Namespace(doc);
        var xpath = $"//{prefix}PlaceholderReference[@Include='{placeholderName}']";
        return doc.SelectSingleNode(xpath, nsMgr) is not null;
    }

    /// <summary>
    /// Splice or replace a &lt;Parameters&gt; override block under a named &lt;PlaceholderReference&gt;.
    /// Both ListName and Key are uppercased on disk; Value is written verbatim (TwinCAT booleans
    /// need "TRUE"/"FALSE"). Idempotent: matching (ListName, Key) parameters are replaced, new ones
    /// appended, the &lt;Parameters&gt; wrapper reused if present. The caller is responsible for
    /// closing the solution before and reopening after, so the DTE picks the change up before the
    /// next File.SaveAll can regenerate the file from a stale in-memory tree.
    /// </summary>
    public static void SetPlaceholderParameters(
        string plcProjPath, string placeholderName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        if (!File.Exists(plcProjPath))
        {
            throw new InvalidOperationException($"PLC project file not found: {plcProjPath}");
        }

        var doc = Load(plcProjPath);
        var defaultNs = doc.DocumentElement?.NamespaceURI ?? "";
        var (nsMgr, prefix) = Namespace(doc);

        var placeholder = doc.SelectSingleNode($"//{prefix}PlaceholderReference[@Include='{placeholderName}']", nsMgr)
            ?? throw new InvalidOperationException(
                $"PlaceholderReference '{placeholderName}' not found in {plcProjPath}.");

        var wrapper = placeholder.SelectSingleNode($"{prefix}Parameters", nsMgr);
        if (wrapper is null)
        {
            wrapper = doc.CreateElement("Parameters", defaultNs);
            placeholder.AppendChild(wrapper);
        }

        foreach (var (rawListName, keys) in parameters)
        {
            var listName = rawListName.ToUpperInvariant();
            foreach (var (rawKey, value) in keys)
            {
                var key = rawKey.ToUpperInvariant();

                // <Parameter> children sit in the empty namespace ("xmlns=''") while their
                // <Parameters> parent is MSBuild-namespaced, so match on local names.
                var existing = wrapper.ChildNodes.Cast<XmlNode>().FirstOrDefault(cand =>
                    cand.NodeType == XmlNodeType.Element
                    && cand.LocalName == "Parameter"
                    && (cand as XmlElement)?.GetAttribute("ListName") == listName
                    && cand.ChildNodes.Cast<XmlNode>().Any(c =>
                        c.NodeType == XmlNodeType.Element && c.LocalName == "Key" && c.InnerText == key));
                if (existing is not null)
                {
                    wrapper.RemoveChild(existing);
                }

                var paramElem = doc.CreateElement("Parameter", "");
                paramElem.SetAttribute("ListName", listName);
                var keyElem = doc.CreateElement("Key", "");
                keyElem.InnerText = key;
                var valueElem = doc.CreateElement("Value", "");
                valueElem.InnerText = value;
                paramElem.AppendChild(keyElem);
                paramElem.AppendChild(valueElem);
                wrapper.AppendChild(paramElem);
            }
        }

        doc.Save(plcProjPath);
    }

    private static XmlDocument Load(string path)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        return doc;
    }

    /// <summary>The MSBuild namespace manager + the XPath prefix ("m:" when namespaced, else "").</summary>
    private static (XmlNamespaceManager Manager, string Prefix) Namespace(XmlDocument doc)
    {
        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        var defaultNs = doc.DocumentElement?.NamespaceURI ?? "";
        if (string.IsNullOrEmpty(defaultNs))
        {
            return (nsMgr, "");
        }

        nsMgr.AddNamespace("m", defaultNs);
        return (nsMgr, "m:");
    }
}
