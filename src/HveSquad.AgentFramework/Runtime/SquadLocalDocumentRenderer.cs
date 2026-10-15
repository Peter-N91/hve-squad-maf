using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// Optional credential-free local reference renderer using only the .NET runtime.
/// Word uses real OOXML paragraphs/styles/tables; PDF uses paginated WinAnsi text.
/// Unicode outside that PDF font explicitly requests Markdown fallback; hosts may register a Unicode-capable renderer instead.
/// No script, model, network call, temporary source file or state writer is used.
/// </summary>
public sealed class SquadLocalDocumentRenderer : ISquadDocumentRenderer
{
    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<string> SupportedFormats =
        Array.AsReadOnly(new[] { "pdf", "docx" });
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <inheritdoc/>
    public string Binding => "native-local-document-renderer:v1";
    /// <inheritdoc/>
    public IReadOnlyList<string> Formats => SupportedFormats;

    /// <inheritdoc/>
    public ValueTask<SquadDocumentRendering> RenderAsync(SquadDocumentRenderingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Format == "pdf" && request.PlainText.Any(character =>
            character is not ('\r' or '\n' or '\t') && (character < 32 || character > 255 || character is >= '\u0080' and <= '\u009f')))
        {
            return ValueTask.FromResult(new SquadDocumentRendering(null, request.InputSha256,
                "The local reference PDF font cannot represent this document's Unicode text. Register a Unicode-capable local renderer."));
        }
        var bytes = request.Format switch
        {
            "pdf" => Pdf(request.PlainText, cancellationToken),
            "docx" => WordDocument(request, cancellationToken),
            _ => throw new ArgumentException("The local reference renderer supports only pdf and docx.", nameof(request)),
        };
        ReportFormats.Verify(request.Format, bytes);
        return ValueTask.FromResult(new SquadDocumentRendering(bytes, request.InputSha256));
    }

    private static byte[] WordDocument(SquadDocumentRenderingRequest request, CancellationToken token)
    {
        var body = new XElement(Word + "body", Paragraph(request.Title, "Title"));
        var lines = request.Markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var inMetadata = lines[0] == "---";
        var inCode = false;
        var codeFence = "";
        for (var index = inMetadata ? 1 : 0; index < lines.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[index];
            if (inMetadata)
            {
                inMetadata = line != "---";
                continue;
            }
            if (inCode)
            {
                if (line == codeFence)
                {
                    inCode = false;
                }
                else
                {
                    body.Add(Paragraph(line, "Code"));
                }
                continue;
            }
            var fence = Regex.Match(line, @"^(`{3,})(?:text)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (fence.Success)
            {
                inCode = true;
                codeFence = fence.Groups[1].Value;
                continue;
            }
            if (line.StartsWith('|'))
            {
                var rows = new List<string[]>();
                while (index < lines.Length && lines[index].StartsWith('|'))
                {
                    var cells = Regex.Split(lines[index].Trim().Trim('|'), @"(?<!\\)\|",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(cell => Inline(cell.Trim())).ToArray();
                    if (!cells.All(cell => cell.All(character => character is '-' or ':' or ' ')))
                    {
                        rows.Add(cells);
                    }
                    index++;
                }
                index--;
                if (rows.Count != 0)
                {
                    body.Add(Table(rows));
                }
                continue;
            }
            var heading = Regex.Match(line, @"^(#{2,4}) (.*)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (heading.Success)
            {
                body.Add(Paragraph(Inline(heading.Groups[2].Value), "Heading" + (heading.Groups[1].Length - 1).ToString(CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("* ", StringComparison.Ordinal))
            {
                body.Add(Paragraph(Inline(line[2..]), numbering: true));
            }
            else
            {
                body.Add(Paragraph(Inline(line)));
            }
        }
        body.Add(new XElement(Word + "sectPr",
            new XElement(Word + "pgSz", new XAttribute(Word + "w", 12240), new XAttribute(Word + "h", 15840)),
            new XElement(Word + "pgMar", new XAttribute(Word + "top", 1440), new XAttribute(Word + "right", 1440),
                new XAttribute(Word + "bottom", 1440), new XAttribute(Word + "left", 1440),
                new XAttribute(Word + "header", 720), new XAttribute(Word + "footer", 720), new XAttribute(Word + "gutter", 0))));
        var document = new XDocument(new XElement(Word + "document", new XAttribute(XNamespace.Xmlns + "w", Word), body));
        var types = new XDocument(new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            ContentType("/word/document.xml", "document.main"), ContentType("/word/styles.xml", "styles"),
            ContentType("/word/numbering.xml", "numbering")));
        var relationships = new XDocument(new XElement(Relationships + "Relationships",
            Relationship("rId1", "officeDocument", "word/document.xml")));
        var documentRelationships = new XDocument(new XElement(Relationships + "Relationships",
            Relationship("rId1", "styles", "styles.xml"), Relationship("rId2", "numbering", "numbering.xml")));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WritePart(archive, "[Content_Types].xml", types);
            WritePart(archive, "_rels/.rels", relationships);
            WritePart(archive, "word/document.xml", document);
            WritePart(archive, "word/_rels/document.xml.rels", documentRelationships);
            WritePart(archive, "word/styles.xml", Styles());
            WritePart(archive, "word/numbering.xml", Numbering());
        }
        return output.ToArray();
    }

    private static XElement ContentType(string part, string type) =>
        new(ContentTypes + "Override", new XAttribute("PartName", part),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml." + type + "+xml"));

    private static XElement Relationship(string id, string type, string target) =>
        new(Relationships + "Relationship", new XAttribute("Id", id),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type),
            new XAttribute("Target", target));

    private static void WritePart(ZipArchive archive, string name, XDocument document)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(stream);
    }

    private static XElement Paragraph(string text, string? style = null, bool numbering = false)
    {
        var properties = new XElement(Word + "pPr");
        if (style is not null)
        {
            properties.Add(new XElement(Word + "pStyle", new XAttribute(Word + "val", style)));
        }
        if (numbering)
        {
            properties.Add(new XElement(Word + "numPr", new XElement(Word + "ilvl", new XAttribute(Word + "val", 0)),
                new XElement(Word + "numId", new XAttribute(Word + "val", 1))));
        }
        return new(Word + "p", properties, new XElement(Word + "r",
            new XElement(Word + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    }

    private static XElement Table(List<string[]> rows)
    {
        var columns = rows[0].Length;
        if (columns == 0 || rows.Any(row => row.Length != columns))
        {
            throw new InvalidDataException("The local Word table needs a complete fixed-width row shape.");
        }
        var widths = Enumerable.Range(0, columns).Select(index => index == columns - 1
            ? 9360 - (9360 / columns * (columns - 1)) : 9360 / columns).ToArray();
        var table = new XElement(Word + "tbl",
            new XElement(Word + "tblPr", new XElement(Word + "tblW", new XAttribute(Word + "w", 9360), new XAttribute(Word + "type", "dxa")),
                new XElement(Word + "tblLayout", new XAttribute(Word + "type", "fixed"))),
            new XElement(Word + "tblGrid", widths.Select(width => new XElement(Word + "gridCol", new XAttribute(Word + "w", width)))));
        foreach (var row in rows)
        {
            table.Add(new XElement(Word + "tr", row.Select((cell, index) =>
                new XElement(Word + "tc", new XElement(Word + "tcPr",
                    new XElement(Word + "tcW", new XAttribute(Word + "w", widths[index]), new XAttribute(Word + "type", "dxa")),
                    new XElement(Word + "tcMar", new XElement(Word + "top", new XAttribute(Word + "w", 80), new XAttribute(Word + "type", "dxa")),
                        new XElement(Word + "left", new XAttribute(Word + "w", 120), new XAttribute(Word + "type", "dxa")),
                        new XElement(Word + "bottom", new XAttribute(Word + "w", 80), new XAttribute(Word + "type", "dxa")),
                        new XElement(Word + "right", new XAttribute(Word + "w", 120), new XAttribute(Word + "type", "dxa")))),
                    Paragraph(cell)))));
        }
        return table;
    }

    private static XDocument Styles()
    {
        var styles = new XElement(Word + "styles",
            new XElement(Word + "docDefaults", new XElement(Word + "rPrDefault", new XElement(Word + "rPr",
                new XElement(Word + "rFonts", new XAttribute(Word + "ascii", "Arial"), new XAttribute(Word + "hAnsi", "Arial")),
                new XElement(Word + "sz", new XAttribute(Word + "val", 22))))));
        foreach (var (id, size, outline) in new[] { ("Normal", 22, -1), ("Title", 36, -1),
            ("Heading1", 32, 0), ("Heading2", 28, 1), ("Heading3", 24, 2), ("Code", 20, -1) })
        {
            styles.Add(new XElement(Word + "style", new XAttribute(Word + "type", "paragraph"), new XAttribute(Word + "styleId", id),
                new XElement(Word + "name", new XAttribute(Word + "val", id)),
                id == "Normal" ? null : new XElement(Word + "basedOn", new XAttribute(Word + "val", "Normal")),
                new XElement(Word + "pPr", new XElement(Word + "spacing", new XAttribute(Word + "before", outline >= 0 ? 180 : 0),
                    new XAttribute(Word + "after", 120)), outline < 0 ? null : new XElement(Word + "outlineLvl", new XAttribute(Word + "val", outline))),
                new XElement(Word + "rPr", id.StartsWith("Heading", StringComparison.Ordinal) ? new XElement(Word + "b") : null,
                    id == "Code" ? new XElement(Word + "rFonts", new XAttribute(Word + "ascii", "Consolas"), new XAttribute(Word + "hAnsi", "Consolas")) : null,
                    new XElement(Word + "sz", new XAttribute(Word + "val", size)))));
        }
        return new(styles);
    }

    private static XDocument Numbering() => new(new XElement(Word + "numbering",
        new XElement(Word + "abstractNum", new XAttribute(Word + "abstractNumId", 0),
            new XElement(Word + "multiLevelType", new XAttribute(Word + "val", "singleLevel")),
            new XElement(Word + "lvl", new XAttribute(Word + "ilvl", 0),
                new XElement(Word + "start", new XAttribute(Word + "val", 1)),
                new XElement(Word + "numFmt", new XAttribute(Word + "val", "bullet")),
                new XElement(Word + "lvlText", new XAttribute(Word + "val", "\u2022")),
                new XElement(Word + "lvlJc", new XAttribute(Word + "val", "left")),
                new XElement(Word + "pPr", new XElement(Word + "ind", new XAttribute(Word + "left", 720), new XAttribute(Word + "hanging", 360))))),
        new XElement(Word + "num", new XAttribute(Word + "numId", 1),
            new XElement(Word + "abstractNumId", new XAttribute(Word + "val", 0)))));

    private static string Inline(string text) => WebUtility.HtmlDecode(Regex.Replace(text, @"\\([\\|\[\]*`#])", "$1",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    private static byte[] Pdf(string text, CancellationToken token)
    {
        var lines = new List<string>();
        foreach (var source in text.Replace("\r", "", StringComparison.Ordinal).Replace("\t", "    ", StringComparison.Ordinal).Split('\n'))
        {
            var rest = source;
            while (rest.Length > 88)
            {
                var split = rest.LastIndexOf(' ', 88);
                if (split < 1)
                {
                    split = 88;
                }
                lines.Add(rest[..split]);
                rest = rest[split..].TrimStart(' ');
            }
            lines.Add(rest);
        }
        var pages = lines.Chunk(48).ToArray();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages.Length)
                .Select(index => (4 + index * 2).ToString(CultureInfo.InvariantCulture) + " 0 R")) +
                "] /Count " + pages.Length.ToString(CultureInfo.InvariantCulture) + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            var content = "BT\n/F1 11 Tf\n14 TL\n54 738 Td\n" +
                string.Concat(page.Select(line => "(" + line.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ") Tj\nT*\n")) + "ET\n";
            var contentId = objects.Count + 2;
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents " +
                contentId.ToString(CultureInfo.InvariantCulture) + " 0 R >>");
            objects.Add("<< /Length " + Encoding.Latin1.GetByteCount(content).ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "endstream");
        }
        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.Latin1.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        foreach (var value in objects)
        {
            token.ThrowIfCancellationRequested();
            offsets.Add(output.Position);
            Write((offsets.Count - 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + value + "\nendobj\n");
        }
        var xref = output.Position;
        Write("xref\n0 " + offsets.Count.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }
        Write("trailer\n<< /Size " + offsets.Count.ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R >>\nstartxref\n" +
            xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return output.ToArray();
    }
}
