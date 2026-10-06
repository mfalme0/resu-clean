using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ResuClean.Services;

/// <summary>
/// Resume text extraction for pasted text, .txt, .docx and .pdf, plus a minimal .docx writer.
/// Extraction is deliberately paragraph-oriented so headings stay on their own lines for the ATS rules.
/// </summary>
public static class Extractor
{
    public sealed record ExtractedText(string Text, IReadOnlyList<string> Warnings);

    public static ExtractedText FromPlainText(string text)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            warnings.Add("No text was provided.");
        return new ExtractedText(text, warnings);
    }

    public static ExtractedText FromTxt(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) warnings.Add("The .txt file is empty.");
        return new ExtractedText(text, warnings);
    }

    public static ExtractedText FromDocx(Stream stream)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder();

        using (stream)
        {
            try
            {
                using var doc = WordprocessingDocument.Open(stream, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is null)
                {
                    warnings.Add("The .docx has no readable document body.");
                    return new ExtractedText(string.Empty, warnings);
                }

                foreach (var element in body.ChildElements)
                {
                    switch (element)
                    {
                        case Paragraph p:
                            AppendParagraph(sb, p, warnings);
                            break;
                        case Table table:
                            warnings.Add("The .docx contains a table. Tables confuse some ATS parsers; its text was flattened in reading order.");
                            foreach (var row in table.Elements<TableRow>())
                            {
                                var cells = row.Elements<TableCell>()
                                    .Select(cell => string.Concat(cell.Descendants<Text>().Select(t => t.Text)).Trim())
                                    .Where(s => s.Length > 0)
                                    .ToList();
                                if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells));
                            }
                            sb.AppendLine();
                            break;
                    }
                }
            }
            catch (OpenXmlPackageException ex)
            {
                warnings.Add($"Could not read the .docx ({ex.Message}). Re-save it from Word as .docx, or paste the text instead.");
                return new ExtractedText(string.Empty, warnings);
            }
        }

        if (string.IsNullOrWhiteSpace(sb.ToString())) warnings.Add("No text was found in the .docx.");
        return new ExtractedText(sb.ToString(), warnings);
    }

    private static void AppendParagraph(StringBuilder sb, Paragraph p, List<string> warnings)
    {
        var text = string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim();
        if (text.Length == 0)
        {
            // Blank paragraph: keep at most one blank line between blocks.
            if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append('\n');
            return;
        }

        // Detect a heading paragraph (bold, short, no trailing colon) so we keep it on its own line.
        var isBold = p.Descendants<Run>().Any(r =>
        {
            var b = r.RunProperties?.Bold;
            return b != null && (b.Val == null || b.Val.Value);
        });
        var looksLikeHeading = text.Length <= 60 && !text.EndsWith('.') && !text.Contains(": ");
        if (isBold && looksLikeHeading) text = text.ToUpperInvariant();

        sb.AppendLine(text);
    }

    public static ExtractedText FromPdf(Stream stream)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder();

        using (stream)
        {
            try
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(stream);
                var pageIndex = 0;
                foreach (var page in pdf.GetPages())
                {
                    pageIndex++;
                    if (pageIndex > 25)
                    {
                        warnings.Add("Only the first 25 PDF pages were read.");
                        break;
                    }
                    var content = page.Text;
                    if (!string.IsNullOrWhiteSpace(content)) sb.AppendLine(content);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read the PDF ({ex.Message}). If it is a scanned image, export a text PDF or paste the text instead.");
                return new ExtractedText(string.Empty, warnings);
            }
        }

        if (string.IsNullOrWhiteSpace(sb.ToString()))
            warnings.Add("No text layer was found in the PDF. Scanned images have no extractable text; paste it instead.");

        return new ExtractedText(sb.ToString(), warnings);
    }

    public static ExtractedText Extract(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".text" or ".md" or ".rtf" => FromTxt(stream),
            ".docx" => FromDocx(stream),
            ".pdf" => FromPdf(stream),
            _ => throw new ArgumentException($"Unsupported file type '{ext}'. Use .txt, .docx or .pdf.")
        };
    }

    // ---- Writer -------------------------------------------------------------

    /// <summary>Writes resume text to a .docx. Single column, standard fonts, no tables: ATS-safe by construction.</summary>
    public static void WriteDocx(string path, string text, string? fullName = null)
    {
        using var stream = File.Create(path);
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                body.Append(new Paragraph());
                continue;
            }

            var isHeading = LooksLikeHeading(line);
            var paragraph = new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { After = isHeading ? "120" : "60", Before = isHeading ? "240" : "0", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
                new Run(
                    new RunProperties(
                        new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                        new FontSize { Val = "22" },
                        isHeading ? new Bold() : null!),
                    new Text(line) { Space = SpaceProcessingModeValues.Preserve }));

            body.Append(paragraph);
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var props = doc.PackageProperties;
            props.Title = fullName;
            props.Creator = "resu-clean";
        }

        main.Document.Save();
    }

    private static bool LooksLikeHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is 0 or > 60) return false;
        var letters = trimmed.Where(char.IsLetter).ToArray();
        if (letters.Length < 3) return false;
        var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        if (upperRatio > 0.7) return true;
        var known = new[] { "summary", "profile", "objective", "experience", "education", "skills", "projects", "certifications", "awards", "languages", "references" };
        var lower = trimmed.ToLowerInvariant();
        return known.Any(k => lower == k || lower.StartsWith(k + " ", StringComparison.Ordinal));
    }
}