using System.Xml;

namespace TcKit.Core.Authoring;

/// <summary>
/// File-only (.plcproj) helpers shared by both writer backends: .plcproj resolution by PLC name
/// and library reference/placeholder parameter blocks. For the automation backend this is the
/// library-parameter lane's escape hatch (the Automation Interface exposes no documented surface
/// for placeholder *parameter overrides*: the IDE's "Library Parameters" dialog has no
/// programmatic counterpart on ITcPlcLibraryManager / ITcPlcPlaceholderRef, and the placeholder
/// tree item's ConsumeXml schema is undocumented, so the MSBuild XML that XAE itself writes on
/// disk is the only reliable target). For the XML backend it is simply part of the on-disk write
/// path. COM-free, so it is unit-tested against temp files. Mirrors Find-TcPlcProjFile /
/// Test-TcPlcProjHasPlaceholder / Set-TcPlcProjPlaceholderParameters in bridge/harness/_TcDte.psm1.
/// </summary>
public static class PlcProjXml
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

    /// <summary>The reference element kinds that take &lt;Parameters&gt; blocks.</summary>
    public const string PlaceholderElement = "PlaceholderReference";
    public const string LibraryElement = "LibraryReference";

    /// <summary>True when the .plcproj already declares a &lt;PlaceholderReference Include="name"&gt;.</summary>
    public static bool HasPlaceholder(string plcProjPath, string placeholderName)
        => HasReference(plcProjPath, PlaceholderElement, placeholderName);

    /// <summary>
    /// True when the .plcproj declares the named reference. Placeholder Includes are the bare name;
    /// LibraryReference Includes are "Name,Version,Distributor", matched on the name segment.
    /// </summary>
    public static bool HasReference(string plcProjPath, string elementName, string referenceName)
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

        return FindReference(doc, elementName, referenceName) is not null;
    }

    /// <summary>
    /// True when the named reference carries every given (ListName, Key, Value) parameter on disk.
    /// False when the file, the reference, or any parameter is missing — the signal the guard uses
    /// to detect an XAE save that regenerated the file from a stale in-memory tree.
    /// </summary>
    public static bool HasParameters(
        string plcProjPath, string elementName, string referenceName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
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
#pragma warning disable CA1031 // A malformed file reads as "parameters missing"; the restore path will surface the real error.
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031

        if (FindReference(doc, elementName, referenceName) is not { } reference)
        {
            return false;
        }

        var wrapper = reference.ChildNodes.Cast<XmlNode>()
            .FirstOrDefault(c => c.NodeType == XmlNodeType.Element && c.LocalName == "Parameters");
        if (wrapper is null)
        {
            return false;
        }

        foreach (var (rawListName, keys) in parameters)
        {
            var listName = rawListName.ToUpperInvariant();
            foreach (var (rawKey, value) in keys)
            {
                var key = rawKey.ToUpperInvariant();
                var found = wrapper.ChildNodes.Cast<XmlNode>().Any(cand =>
                    cand.NodeType == XmlNodeType.Element
                    && cand.LocalName == "Parameter"
                    && (cand as XmlElement)?.GetAttribute("ListName") == listName
                    && cand.ChildNodes.Cast<XmlNode>().Any(c =>
                        c.NodeType == XmlNodeType.Element && c.LocalName == "Key" && c.InnerText == key)
                    && cand.ChildNodes.Cast<XmlNode>().Any(c =>
                        c.NodeType == XmlNodeType.Element && c.LocalName == "Value" && c.InnerText == value));
                if (!found)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Splice or replace a &lt;Parameters&gt; override block under a named &lt;PlaceholderReference&gt;.
    /// See <see cref="SetReferenceParameters"/> for semantics.
    /// </summary>
    public static void SetPlaceholderParameters(
        string plcProjPath, string placeholderName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
        => SetReferenceParameters(plcProjPath, PlaceholderElement, placeholderName, parameters);

    /// <summary>
    /// Splice or replace a &lt;Parameters&gt; override block under a named reference element.
    /// Both ListName and Key are uppercased on disk; Value is written verbatim (TwinCAT booleans
    /// need "TRUE"/"FALSE"). Idempotent: matching (ListName, Key) parameters are replaced, new ones
    /// appended, the &lt;Parameters&gt; wrapper reused if present. The caller is responsible for
    /// closing the solution before and reopening after, so the DTE picks the change up before the
    /// next File.SaveAll can regenerate the file from a stale in-memory tree.
    /// </summary>
    public static void SetReferenceParameters(
        string plcProjPath, string elementName, string referenceName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        if (!File.Exists(plcProjPath))
        {
            throw new InvalidOperationException($"PLC project file not found: {plcProjPath}");
        }

        var doc = Load(plcProjPath);
        var defaultNs = doc.DocumentElement?.NamespaceURI ?? "";

        var placeholder = (XmlNode?)FindReference(doc, elementName, referenceName)
            ?? throw new InvalidOperationException(
                $"{elementName} '{referenceName}' not found in {plcProjPath}.");

        var wrapper = placeholder.ChildNodes.Cast<XmlNode>()
            .FirstOrDefault(c => c.NodeType == XmlNodeType.Element && c.LocalName == "Parameters");
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

    /// <summary>
    /// Find a reference element by name, namespace-agnostic (matching on local names, since XAE
    /// writes MSBuild-namespaced parents with empty-namespace children). PlaceholderReference
    /// Includes are the bare name; LibraryReference Includes are "Name,Version,Distributor", so the
    /// name segment before the first comma is what identifies the library.
    /// </summary>
    private static XmlElement? FindReference(XmlDocument doc, string elementName, string referenceName)
        => doc.SelectNodes($"//*[local-name()='{elementName}']")!
            .OfType<XmlElement>()
            .FirstOrDefault(e =>
            {
                var include = e.GetAttribute("Include");
                var name = include.Split(',')[0].Trim();
                return string.Equals(name, referenceName, StringComparison.OrdinalIgnoreCase);
            });
}
