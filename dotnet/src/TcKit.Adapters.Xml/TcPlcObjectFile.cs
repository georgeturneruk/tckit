using System.Xml;
using TcKit.Core.Models;

namespace TcKit.Adapters.Xml;

/// <summary>
/// One TwinCAT object file (.TcPOU / .TcIO / .TcGVL / .TcDUT) opened for editing: the container
/// element (POU / Itf / GVL / DUT), its Declaration / Implementation CDATA, and the member
/// elements (Method / Action / Property with Get / Set accessors). Loaded PreserveWhitespace so
/// untouched regions (including LineIds XAE may have written) round-trip byte-identically; new
/// nodes are inserted with explicit indentation via <see cref="TcXmlFormat"/>.
/// </summary>
internal sealed class TcPlcObjectFile
{
    private static readonly string[] s_namedMemberTags = ["Method", "Action", "Property"];
    private static readonly string[] s_accessorTags = ["Get", "Set"];

    private readonly XmlDocument _doc;
    private readonly XmlElement _container;

    public string FilePath { get; }

    public TcXmlFormat.FileStyle Style { get; }

    /// <summary>POU, Itf, GVL, or DUT.</summary>
    public string ContainerTag => _container.LocalName;

    public string Name => _container.GetAttribute("Name");

    private TcPlcObjectFile(XmlDocument doc, XmlElement container, string filePath, TcXmlFormat.FileStyle style)
    {
        _doc = doc;
        _container = container;
        FilePath = filePath;
        Style = style;
    }

    public static TcPlcObjectFile Load(string path)
    {
        var doc = TcXmlFormat.LoadFile(path);
        var container = doc.DocumentElement?.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName is "POU" or "Itf" or "GVL" or "DUT")
            ?? throw new InvalidDataException($"No <POU>, <Itf>, <GVL>, or <DUT> element found in {path}");
        return new TcPlcObjectFile(doc, container, path, TcXmlFormat.DetectStyle(path));
    }

    /// <summary>Create a new POU file (an .TcIO with an &lt;Itf&gt; root for interfaces).</summary>
    public static TcPlcObjectFile CreatePou(string path, string name, PouType pouType, string declaration, string implementation)
    {
        var doc = TcXmlFormat.NewTcPlcObject(out var root);
        var isInterface = pouType == PouType.Interface;
        var container = doc.CreateElement(isInterface ? "Itf" : "POU");
        container.SetAttribute("Name", name);
        container.SetAttribute("Id", GuidSource.NewId());
        if (!isInterface)
        {
            container.SetAttribute("SpecialFunc", "None");
        }

        TcXmlFormat.AppendIndented(root, container, 1);
        var file = new TcPlcObjectFile(doc, container, path, TcXmlFormat.FileStyle.Canonical);
        file.SetDeclarationOn(container, declaration);
        if (!isInterface)
        {
            file.SetImplementationOn(container, implementation);
        }

        return file;
    }

    /// <summary>Create a new declaration-only file (.TcGVL or .TcDUT).</summary>
    public static TcPlcObjectFile CreateDeclarationOnly(string path, string tag, string name, string declaration)
    {
        var doc = TcXmlFormat.NewTcPlcObject(out var root);
        var container = doc.CreateElement(tag);
        container.SetAttribute("Name", name);
        container.SetAttribute("Id", GuidSource.NewId());
        TcXmlFormat.AppendIndented(root, container, 1);
        var file = new TcPlcObjectFile(doc, container, path, TcXmlFormat.FileStyle.Canonical);
        file.SetDeclarationOn(container, declaration);
        return file;
    }

    /// <summary>GVLs and DUTs carry no implementation; writes to one must be refused.</summary>
    public bool IsDeclarationOnly => ContainerTag is "GVL" or "DUT";

    /// <summary>Interface POUs take declaration-only members (same detection as the reader).</summary>
    public bool IsInterface
        => ContainerTag is "POU" or "Itf"
            && TcFileParser.DetectPouType(Declaration, ContainerTag) == PouType.Interface;

    public string Declaration
    {
        get => TextOf(ChildElement(_container, "Declaration"));
        set => SetDeclarationOn(_container, value);
    }

    public string Implementation
    {
        get => TextOf(ChildElement(ChildElement(_container, "Implementation"), "ST"));
        set
        {
            if (IsDeclarationOnly)
            {
                throw new InvalidOperationException(
                    $"'{Name}' is declaration-only ({ContainerTag}); it has no implementation to set.");
            }

            SetImplementationOn(_container, value);
        }
    }

    /// <summary>
    /// Depth-first member lookup, mirroring the automation lane's FindChild: Method / Action /
    /// Property match on their Name attribute, property accessors on the literal names Get / Set.
    /// </summary>
    public XmlElement? FindMember(string name)
    {
        foreach (var element in Descendants(_container))
        {
            if (s_namedMemberTags.Contains(element.LocalName) && element.GetAttribute("Name") == name)
            {
                return element;
            }

            if (s_accessorTags.Contains(element.LocalName) && element.LocalName == name)
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>Append a Method element; interface methods get no Implementation element.</summary>
    public XmlElement AddMethod(string name, string declaration, string? implementation)
    {
        var method = _doc.CreateElement("Method");
        method.SetAttribute("Name", name);
        method.SetAttribute("Id", GuidSource.NewId());
        TcXmlFormat.AppendIndented(_container, method, 2);
        SetDeclarationOn(method, declaration);
        if (implementation is not null)
        {
            SetImplementationOn(method, implementation);
        }

        return method;
    }

    /// <summary>Append a Property element (accessors added separately).</summary>
    public XmlElement AddProperty(string name, string declaration)
    {
        var property = _doc.CreateElement("Property");
        property.SetAttribute("Name", name);
        property.SetAttribute("Id", GuidSource.NewId());
        TcXmlFormat.AppendIndented(_container, property, 2);
        SetDeclarationOn(property, declaration);
        return property;
    }

    /// <summary>Append a Get or Set accessor under a Property element (Name="Get"/"Set", as XAE writes).</summary>
    public XmlElement AddAccessor(XmlElement property, string kind, string declaration, string? implementation)
    {
        var accessor = _doc.CreateElement(kind);
        accessor.SetAttribute("Name", kind);
        accessor.SetAttribute("Id", GuidSource.NewId());
        TcXmlFormat.AppendIndented(property, accessor, 3);
        SetDeclarationOn(accessor, declaration);
        if (implementation is not null)
        {
            SetImplementationOn(accessor, implementation);
        }

        return accessor;
    }

    /// <summary>Remove a member (or accessor) element along with its indentation.</summary>
    public static void RemoveMember(XmlElement member) => TcXmlFormat.RemoveIndented(member);

    public string DeclarationOf(XmlElement member) => TextOf(ChildElement(member, "Declaration"));

    public string ImplementationOf(XmlElement member)
        => TextOf(ChildElement(ChildElement(member, "Implementation"), "ST"));

    public void SetDeclarationOn(XmlElement member, string text)
    {
        var declaration = ChildElement(member, "Declaration");
        if (declaration is null)
        {
            declaration = _doc.CreateElement("Declaration");
            var successor = member.ChildNodes.OfType<XmlElement>().FirstOrDefault();
            if (successor is not null)
            {
                // A Declaration always precedes Implementation / members (XAE's element order).
                TcXmlFormat.InsertIndentedBefore(member, declaration, successor, Depth(member) + 1);
            }
            else
            {
                TcXmlFormat.AppendIndented(member, declaration, Depth(member) + 1);
            }
        }

        ReplaceCdata(declaration, text);
    }

    public void SetImplementationOn(XmlElement member, string text)
    {
        var implementation = ChildElement(member, "Implementation");
        if (implementation is null)
        {
            implementation = _doc.CreateElement("Implementation");
            TcXmlFormat.AppendIndented(member, implementation, Depth(member) + 1);
        }

        var st = ChildElement(implementation, "ST");
        if (st is null)
        {
            st = _doc.CreateElement("ST");
            TcXmlFormat.AppendIndented(implementation, st, Depth(implementation) + 1);
        }

        ReplaceCdata(st, text);
    }

    public void Save() => TcXmlFormat.Save(_doc, FilePath, Style);

    private void ReplaceCdata(XmlElement element, string text)
    {
        while (element.FirstChild is not null)
        {
            element.RemoveChild(element.FirstChild);
        }

        element.AppendChild(TcXmlFormat.Cdata(_doc, text));
    }

    private static string TextOf(XmlElement? element)
        => element is null ? "" : string.Concat(element.ChildNodes.OfType<XmlCharacterData>().Select(n => n.Value));

    private static XmlElement? ChildElement(XmlElement? parent, string localName)
        => parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName);

    private static int Depth(XmlElement element)
    {
        var depth = 0;
        for (XmlNode? node = element; node.ParentNode is XmlElement parent; node = parent)
        {
            depth++;
        }

        return depth;
    }

    private static IEnumerable<XmlElement> Descendants(XmlElement root)
    {
        foreach (var child in root.ChildNodes.OfType<XmlElement>())
        {
            yield return child;
            foreach (var grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }
}
