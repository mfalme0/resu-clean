using System.Text;
using System.Text.RegularExpressions;
using ResuClean.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ResuClean.Services.Kits;

/// <summary>
/// QuestPDF document: an ATS-safe single-column resume. Text is written from the resume version
/// only; nothing here adds content.
/// </summary>
public static class PdfWriter
{
    private static bool _initialised;

    public static void Initialise()
    {
        if (_initialised) return;
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
        _initialised = true;
    }

    private static readonly string[] Headings =
    {
        "summary", "profile", "objective", "experience", "work experience", "employment", "education",
        "skills", "technical skills", "projects", "certifications", "awards", "languages", "references",
        "professional experience", "core competencies"
    };

    public static void Write(string path, string text, string fullName)
    {
        Initialise();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(18, Unit.Millimetre);
                page.PageColor(Colors.White);

                page.DefaultTextStyle(t => t.FontFamily("Calibri").FontSize(10.5f).LineHeight(1.25f));

                page.Content().Column(column =>
                {
                    foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                    {
                        var line = raw.TrimEnd();
                        if (line.Length == 0)
                        {
                            column.Item().PaddingVertical(2).LineHorizontal(0.4f).LineColor(Colors.Grey.Lighten2);
                            continue;
                        }

                        if (IsHeading(line))
                        {
                            column.Item().PaddingTop(6).PaddingBottom(2)
                                .Text(line.ToUpperInvariant())
                                .SemiBold().FontSize(11.5f).LetterSpacing(0.4f);
                            continue;
                        }

                        var trimmed = line.TrimStart();
                        var isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("• ");
                        if (isBullet) trimmed = trimmed.Length > 2 ? trimmed[2..] : trimmed;

                        column.Item().PaddingTop(isBullet ? 0 : 1).Row(row =>
                        {
                            if (isBullet) row.RelativeItem().Width(8).Text("•").FontSize(10.5f);
                            row.RelativeItem().Text(trimmed).Justify();
                        });
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span($"{fullName}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    text.Span("  ·  page ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    text.CurrentPageNumber().FontSize(8);
                    text.Span(" of ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    text.TotalPages().FontSize(8);
                });
            });
        }).GeneratePdf(path);
    }

    private static bool IsHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is 0 or > 60) return false;
        var lower = trimmed.ToLowerInvariant();
        if (Headings.Any(h => lower == h || lower.StartsWith(h + " ", StringComparison.Ordinal) || lower.StartsWith(h + ":", StringComparison.Ordinal)))
            return true;
        var letters = trimmed.Where(char.IsLetter).ToArray();
        if (letters.Length < 3) return false;
        return letters.Count(char.IsUpper) / (double)letters.Length > 0.7;
    }

    public static void WriteCoverLetter(string path, string body, string fullName)
    {
        Initialise();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20, Unit.Millimetre);
                page.DefaultTextStyle(t => t.FontFamily("Calibri").FontSize(11).LineHeight(1.4f));
                page.Content().Column(column =>
                {
                    column.Item().Text(fullName).SemiBold().FontSize(13);
                    column.Item().PaddingBottom(8).LineHorizontal(0.6f).LineColor(Colors.Grey.Darken2);
                    foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0)
                        {
                            column.Item().PaddingVertical(3);
                            continue;
                        }
                        column.Item().PaddingBottom(6).Text(line).Justify();
                    }
                });
            });
        }).GeneratePdf(path);
    }
}

/// <summary>
/// Builds the ready-to-send application kit: email text, a .eml with the resume embedded, and a
/// .zip bundle. The tool never sends anything; it only produces files the user sends by hand.
/// </summary>
public sealed partial class KitBuilder
{
    public sealed record KitFiles(
        string Subject,
        string Body,
        string ToAddress,
        string ApplyUrl,
        string ResumeDocxPath,
        string ResumePdfPath,
        string DocxPath,
        string PdfPath,
        string EmlPath,
        string ZipPath,
        string MailtoLink,
        int WordCount,
        IReadOnlyList<string> FactsUsed,
        IReadOnlyList<string> Attachments);

    [GeneratedRegex(@"\s+", RegexOptions.None, 2000)]
    private static partial Regex Collapse();

    public string MailtoLink(string to, string subject, string body) =>
        "mailto:" + Uri.EscapeDataString(to) + "?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body);

    public static int WordCount(string text) =>
        text.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Template email used when no model is configured. Facts only, no placeholders.</summary>
    public static (string Subject, string Body) TemplateEmail(string fullName, string jobTitle, string company, ProfileDto profile, IEnumerable<ProfileFactDto> facts)
    {
        var subject = company.Length > 0
            ? $"Application: {jobTitle} at {company}"
            : $"Application: {jobTitle}";

        var voice = string.IsNullOrWhiteSpace(profile.Voice) ? "professional and concise" : profile.Voice.Trim();
        var strongest = facts
            .Where(f => f.Kind is "achievement" or "role")
            .OrderByDescending(f => f.Text.Length)
            .Take(2)
            .ToList();
        if (strongest.Count == 0) strongest = facts.Take(2).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Dear Hiring Team,");
        sb.AppendLine();
        sb.AppendLine($"I am writing to apply for the {jobTitle}{(company.Length > 0 ? $" at {company}" : "")} role. My background lines up closely with what the posting asks for, and I would welcome the chance to talk.");
        sb.AppendLine();

        if (strongest.Count > 0)
        {
            foreach (var fact in strongest)
                sb.AppendLine($"- {fact.Text.Trim()}");
            sb.AppendLine();
        }

        sb.AppendLine($"I have attached my resume and can share any further detail you need. I wrote this in a {voice} tone to keep it short.");
        sb.AppendLine();
        sb.AppendLine("Kind regards,");
        sb.AppendLine(fullName);

        var body = sb.ToString().TrimEnd() + "\n";
        return (subject, body);
    }

    /// <summary>
    /// Writes the .eml with the PDF resume attached. Uses BodyBuilder + AttachmentCollection so the
    /// PDF is streamed from disk rather than loaded into memory. MimeKit types are fully qualified
    /// because this file's own namespace is ResuClean.Services.Kits.
    /// </summary>
    public static void WriteEml(string path, string fromAddress, string toAddress, string subject, string body, string pdfPath, string pdfName)
    {
        var builder = new global::MimeKit.BodyBuilder
        {
            TextBody = body
        };

        if (File.Exists(pdfPath))
        {
            using var stream = File.OpenRead(pdfPath);
            builder.Attachments.Add(pdfName, stream, new global::MimeKit.ContentType("application", "pdf"));
        }

        var message = new global::MimeKit.MimeMessage
        {
            Body = builder.ToMessageBody()
        };
        if (!string.IsNullOrWhiteSpace(fromAddress)) message.From.Add(new global::MimeKit.MailboxAddress("", fromAddress));
        if (!string.IsNullOrWhiteSpace(toAddress)) message.To.Add(new global::MimeKit.MailboxAddress("", toAddress));
        message.Subject = subject;
        message.Date = DateTimeOffset.Now;
        message.MessageId = $"<resu-clean-{Guid.NewGuid():N}@localhost>";
        message.WriteTo(path);
    }

    /// <summary>Packs the named files into a .zip next to them. Streams, so nothing is buffered twice.</summary>
    public static void Zip(IEnumerable<(string Name, string Path)> entries, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var stream = File.Create(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, path) in entries)
        {
            if (!File.Exists(path)) continue;
            var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var source = File.OpenRead(path);
            source.CopyTo(entryStream);
        }
    }
}