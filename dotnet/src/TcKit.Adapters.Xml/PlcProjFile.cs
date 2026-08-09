using System.Xml;

namespace TcKit.Adapters.Xml;

/// <summary>
/// A .plcproj opened for editing: Compile / Folder item maintenance for structural writes, and
/// library / placeholder reference elements. Loaded PreserveWhitespace and never visits
/// ProjectExtensions, so the opaque XmlArchive blob round-trips byte-identically. Parameter
/// override blocks stay with <c>TcKit.Core.Authoring.PlcProjXml</c> (shared with the automation
/// backend); this class owns only what the XML backend adds on top.
/// </summary>
internal sealed class PlcProjFile
{
    private readonly XmlDocument _doc;
    private readonly string _namespace;

    public string FilePath { get; }

    public TcXmlFormat.FileStyle Style { get; }

    private PlcProjFile(XmlDocument doc, string filePath, TcXmlFormat.FileStyle style)
    {
        _doc = doc;
        FilePath = filePath;
        Style = style;
        _namespace = doc.DocumentElement?.NamespaceURI ?? "";
    }

    public static PlcProjFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"PLC project file not found: {path}");
        }

        return new PlcProjFile(TcXmlFormat.LoadFile(path), path, TcXmlFormat.DetectStyle(path));
    }

    public void Save() => TcXmlFormat.Save(_doc, FilePath, Style);

    // --- Compile / Folder items ----------------------------------------------

    /// <summary>
    /// Add a &lt;Compile Include="..."&gt;&lt;SubType&gt;Code&lt;/SubType&gt;&lt;/Compile&gt; item,
    /// inserted in case-insensitive ordinal Include order among the existing Compile items.
    /// </summary>
    public void AddCompileItem(string include)
    {
        var compile = _doc.CreateElement("Compile", _namespace);
        compile.SetAttribute("Include", include);
        var subType = _doc.CreateElement("SubType", _namespace);
        subType.AppendChild(_doc.CreateTextNode("Code"));
        TcXmlFormat.AppendIndented(compile, subType, 3);

        var group = ItemGroupFor("Compile");
        var successor = group.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName == "Compile"
                && string.Compare(e.GetAttribute("Include"), include, StringComparison.OrdinalIgnoreCase) > 0);
        if (successor is not null)
        {
            TcXmlFormat.InsertIndentedBefore(group, compile, successor, 2);
        }
        else
        {
            TcXmlFormat.AppendIndented(group, compile, 2);
        }
    }

    public bool RemoveCompileItem(string include)
    {
        if (FindItem("Compile", include) is not { } item)
        {
            return false;
        }

        TcXmlFormat.RemoveIndented(item);
        return true;
    }

    public bool HasFolderItem(string include)
        => FindItem("Folder", include) is not null;

    public void AddFolderItem(string include)
    {
        var folder = _doc.CreateElement("Folder", _namespace);
        folder.SetAttribute("Include", include);
        TcXmlFormat.AppendIndented(ItemGroupFor("Folder"), folder, 2);
    }

    /// <summary>Remove every Compile / Folder item at or under a folder path prefix.</summary>
    public void RemoveItemsUnder(string folderInclude)
    {
        var prefix = folderInclude + "\\";
        foreach (var item in Items("Compile").Concat(Items("Folder")).ToList())
        {
            var include = item.GetAttribute("Include");
            if (include.Equals(folderInclude, StringComparison.OrdinalIgnoreCase)
                || include.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                TcXmlFormat.RemoveIndented(item);
            }
        }
    }

    // --- library references / placeholders ------------------------------------

    /// <summary>True when the named reference exists (Include name segment, case-insensitive).</summary>
    public bool HasReference(string elementName, string referenceName)
        => FindReference(elementName, referenceName) is not null;

    /// <summary>
    /// Add a &lt;LibraryReference Include="Name,Version,Distributor"&gt; element. XAE keeps
    /// LibraryReferences in their own ItemGroup and records a "*" version request as "newest"
    /// (both live-verified by the parity oracle).
    /// </summary>
    public void AddLibraryReference(string libraryName, string version, string distributor)
    {
        var recorded = string.IsNullOrEmpty(version) || version == "*" ? "newest" : version;
        var reference = _doc.CreateElement("LibraryReference", _namespace);
        reference.SetAttribute("Include", $"{libraryName},{recorded},{distributor}");
        // XAE derives the namespace from library metadata we cannot see off-Windows; the library
        // name is the overwhelmingly common value (parity checklist item, ADR-0017).
        AppendTextChild(reference, "Namespace", libraryName);
        TcXmlFormat.AppendIndented(ItemGroupFor("LibraryReference"), reference, 2);
    }

    /// <summary>
    /// Remove a library reference by name (and distributor when the Include carries one). Returns
    /// the version recorded in the Include, resolving a "*" request to what is on disk.
    /// </summary>
    public string? RemoveLibraryReference(string libraryName, string version, string distributor)
    {
        var candidates = Items("LibraryReference")
            .Where(e => NameSegment(e.GetAttribute("Include")).Equals(libraryName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(distributor))
        {
            // A 2-part Include carries no distributor segment; treat that as compatible.
            candidates = candidates
                .Where(e =>
                {
                    var recorded = Segment(e.GetAttribute("Include"), 2);
                    return recorded == "" || recorded.Equals(distributor, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        if (!string.IsNullOrEmpty(version) && version is not ("*" or "newest"))
        {
            candidates = candidates
                .Where(e => Segment(e.GetAttribute("Include"), 1).Equals(version, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var chosen = candidates[0];
        var resolved = Segment(chosen.GetAttribute("Include"), 1);
        var group = chosen.ParentNode as XmlElement;
        TcXmlFormat.RemoveIndented(chosen);
        // XAE drops an ItemGroup once its last reference goes; leaving an empty one would churn
        // the parity diff forever after.
        if (group is not null && !group.ChildNodes.OfType<XmlElement>().Any())
        {
            TcXmlFormat.RemoveIndented(group);
        }

        return resolved == "" ? "*" : resolved;
    }

    /// <summary>Add a &lt;PlaceholderReference&gt; with its DefaultResolution and Namespace.</summary>
    public void AddPlaceholder(string placeholderName, string defaultLibrary, string version, string distributor)
    {
        var reference = _doc.CreateElement("PlaceholderReference", _namespace);
        reference.SetAttribute("Include", placeholderName);
        AppendTextChild(reference, "DefaultResolution", $"{defaultLibrary}, {VersionOrStar(version)} ({distributor})");
        AppendTextChild(reference, "Namespace", defaultLibrary);
        TcXmlFormat.AppendIndented(ItemGroupFor("PlaceholderReference"), reference, 2);
    }

    public bool RemovePlaceholder(string placeholderName)
    {
        if (FindReference("PlaceholderReference", placeholderName) is not { } reference)
        {
            return false;
        }

        TcXmlFormat.RemoveIndented(reference);
        return true;
    }

    // --- internals -------------------------------------------------------------

    private static string VersionOrStar(string version) => string.IsNullOrEmpty(version) ? "*" : version;

    private static string NameSegment(string include) => Segment(include, 0);

    private static string Segment(string include, int index)
    {
        var parts = include.Split(',');
        return index < parts.Length ? parts[index].Trim() : "";
    }

    private void AppendTextChild(XmlElement parent, string localName, string text)
    {
        var child = _doc.CreateElement(localName, _namespace);
        child.AppendChild(_doc.CreateTextNode(text));
        TcXmlFormat.AppendIndented(parent, child, 3);
    }

    private IEnumerable<XmlElement> Items(string localName)
        => _doc.SelectNodes($"/*/*[local-name()='ItemGroup']/*[local-name()='{localName}']")!
            .OfType<XmlElement>();

    private XmlElement? FindItem(string localName, string include)
        => Items(localName).FirstOrDefault(e =>
            e.GetAttribute("Include").Equals(include, StringComparison.OrdinalIgnoreCase));

    private XmlElement? FindReference(string elementName, string referenceName)
        => Items(elementName).FirstOrDefault(e =>
            NameSegment(e.GetAttribute("Include")).Equals(referenceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The ItemGroup that already holds items of the wanted kind (falling back through the given
    /// preference order), or a fresh ItemGroup appended after the last existing one.
    /// </summary>
    private XmlElement ItemGroupFor(params string[] preferredKinds)
    {
        foreach (var kind in preferredKinds)
        {
            var owning = Items(kind).FirstOrDefault()?.ParentNode as XmlElement;
            if (owning is not null)
            {
                return owning;
            }
        }

        var root = _doc.DocumentElement!;
        var group = _doc.CreateElement("ItemGroup", _namespace);
        var last = root.ChildNodes.OfType<XmlElement>().LastOrDefault(e => e.LocalName == "ItemGroup");

        // Insert after the last existing ItemGroup (before its next element sibling, whose
        // preceding whitespace then indents the new group); append when there is nothing after.
        var successor = last?.NextSibling;
        while (successor is not null && successor.NodeType != System.Xml.XmlNodeType.Element)
        {
            successor = successor.NextSibling;
        }

        if (successor is XmlElement successorElement)
        {
            TcXmlFormat.InsertIndentedBefore(root, group, successorElement, 1);
        }
        else
        {
            TcXmlFormat.AppendIndented(root, group, 1);
        }

        return group;
    }
}
