using System.Text;
using System.Xml;

namespace TcKit.Adapters.Xml;

/// <summary>
/// The one place that owns how TwinCAT XML is written to disk (ADR-0017 determinism spec). New
/// files get the canonical XAE shape: UTF-8 with BOM, CRLF, two-space indent, CDATA sections.
/// Edits reproduce the file's existing BOM/EOL so a checkout normalised to LF (as this repo's
/// fixtures are) does not get churned to CRLF by a one-line change. Documents are loaded with
/// PreserveWhitespace so untouched regions round-trip byte-identically.
/// </summary>
internal static class TcXmlFormat
{
    public const string TcPlcObjectVersion = "1.1.0.1";

    /// <summary>Byte-level style of a file on disk: BOM presence and line-ending flavour.</summary>
    public sealed record FileStyle(bool Bom, string Eol)
    {
        /// <summary>What XAE itself writes: UTF-8 BOM + CRLF.</summary>
        public static readonly FileStyle Canonical = new(true, "\r\n");
    }

    /// <summary>Detect the BOM/EOL style of an existing file (canonical defaults on ambiguity).</summary>
    public static FileStyle DetectStyle(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                return new FileStyle(bom, i > 0 && bytes[i - 1] == (byte)'\r' ? "\r\n" : "\n");
            }
        }

        return new FileStyle(bom, FileStyle.Canonical.Eol);
    }

    /// <summary>Load a TwinCAT XML file for minimal-diff editing.</summary>
    public static XmlDocument LoadFile(string path)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        try
        {
            doc.Load(path);
        }
        catch (XmlException exc)
        {
            throw new InvalidDataException($"XML parse error in {path}: {exc.Message}", exc);
        }

        return doc;
    }

    /// <summary>
    /// Serialise and write with an explicit style. All line endings (element whitespace and CDATA
    /// content alike) are normalised to the style's EOL, matching XAE's uniform output.
    /// </summary>
    public static void Save(XmlDocument doc, string path, FileStyle style)
    {
        doc.PreserveWhitespace = true;
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            // The declaration's encoding label comes from the writer, so force utf-8 here and
            // handle the BOM ourselves below (UTF8Encoding(false) suppresses the writer's own).
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
        };
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            doc.Save(writer);
        }

        var xml = Encoding.UTF8.GetString(buffer.ToArray())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", style.Eol, StringComparison.Ordinal);

        var payload = Encoding.UTF8.GetBytes(xml);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (style.Bom)
        {
            output.Write(Encoding.UTF8.GetPreamble());
        }

        output.Write(payload);
    }

    /// <summary>A fresh TcPlcObject document with the canonical declaration and root element.</summary>
    public static XmlDocument NewTcPlcObject(out XmlElement root)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        // Nothing auto-indents a PreserveWhitespace document, including the declaration line break.
        doc.AppendChild(doc.CreateWhitespace("\n"));
        root = doc.CreateElement("TcPlcObject");
        root.SetAttribute("Version", TcPlcObjectVersion);
        doc.AppendChild(root);
        return doc;
    }

    /// <summary>Two-space indent for the given element depth.</summary>
    public static string Ind(int depth) => new(' ', depth * 2);

    /// <summary>A newline-plus-indent whitespace node (PreserveWhitespace docs don't auto-indent).</summary>
    public static XmlWhitespace NewLine(XmlDocument doc, int depth) => doc.CreateWhitespace("\n" + Ind(depth));

    /// <summary>
    /// A CDATA section for ST source. Refuses text containing "]]&gt;" outright: split-CDATA
    /// emission would be silent corruption territory, and the sequence cannot occur in valid ST.
    /// </summary>
    public static XmlCDataSection Cdata(XmlDocument doc, string text)
    {
        if (text.Contains("]]>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Code contains \"]]>\", which cannot be stored in a TwinCAT CDATA section.");
        }

        return doc.CreateCDataSection(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Append a child element with surrounding indentation whitespace: the child lands at
    /// <paramref name="depth"/> and the parent's closing tag returns to <c>depth - 1</c>.
    /// </summary>
    public static void AppendIndented(XmlElement parent, XmlElement child, int depth)
    {
        var doc = parent.OwnerDocument!;
        // Drop a trailing whitespace-only node so the new element replaces the old closing indent.
        if (parent.LastChild is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace })
        {
            parent.RemoveChild(parent.LastChild);
        }

        parent.AppendChild(NewLine(doc, depth));
        parent.AppendChild(child);
        parent.AppendChild(NewLine(doc, depth - 1));
    }

    /// <summary>Insert a child element before <paramref name="successor"/>, keeping the indent rhythm.</summary>
    public static void InsertIndentedBefore(XmlElement parent, XmlElement child, XmlNode successor, int depth)
    {
        var doc = parent.OwnerDocument!;
        parent.InsertBefore(child, successor);
        parent.InsertBefore(NewLine(doc, depth), successor);
    }

    /// <summary>Remove an element together with its preceding indentation whitespace.</summary>
    public static void RemoveIndented(XmlElement element)
    {
        var parent = element.ParentNode!;
        if (element.PreviousSibling is { NodeType: XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace } ws)
        {
            parent.RemoveChild(ws);
        }

        parent.RemoveChild(element);
    }
}
