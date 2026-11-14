using System.Globalization;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using MimeKit;
using Wisp.Core.Rendering;

namespace Wisp.Core.Mail;

public enum ExportFormat
{
    /// <summary>The original message, byte for byte. Opens in every mail program and can be imported again.</summary>
    Eml,
    /// <summary>A page for the browser (and from there for printing or saving as PDF).</summary>
    Html,
    /// <summary>Header lines and the readable text.</summary>
    Text,
}

/// <summary>Writes a mail to a file outside of Wisp. The file is not encrypted; where it goes is the user's choice.</summary>
public static partial class MailExporter
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Html => ".html",
        ExportFormat.Text => ".txt",
        _ => ".eml",
    };

    /// <summary>".html"/".htm" and ".txt" pick those formats; anything else is the original (.eml).</summary>
    public static ExportFormat FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" or ".htm" => ExportFormat.Html,
        ".txt" => ExportFormat.Text,
        _ => ExportFormat.Eml,
    };

    /// <summary>"2026-11-09 1012 Betreff" – sorts by date in a folder and is valid on Windows and Linux.</summary>
    public static string FileName(DateTimeOffset date, string? subject, ExportFormat format)
    {
        // A slash in a subject ("10/2026") is not a path: keep what stands before it.
        var title = MailRenderer.SafeFileName(string.IsNullOrWhiteSpace(subject) ? "Ohne Betreff" : subject.Replace('/', '-').Replace('\\', '-').Trim());
        title = string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 80) title = title[..80].TrimEnd(' ', '.');
        return $"{date.ToLocalTime():yyyy-MM-dd HHmm} {title}{Extension(format)}";
    }

    /// <summary>A name not yet in <paramref name="taken"/>: "Name.eml", "Name (2).eml", … The result is added to the set.</summary>
    public static string Unique(string fileName, ISet<string> taken)
    {
        var name = fileName;
        for (var i = 2; !taken.Add(name); i++)
            name = $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}";
        return name;
    }

    /// <param name="keepRemoteImages">HTML only: leave images from the internet in the page. Off, they are taken out,
    /// so opening the file in a browser does not tell the sender that the mail was read.</param>
    public static async Task WriteAsync(byte[] raw, ExportFormat format, Stream target, bool keepRemoteImages, CancellationToken ct)
    {
        if (format == ExportFormat.Eml)
        {
            await target.WriteAsync(raw, ct);
            return;
        }
        var message = MailRenderer.Parse(raw);
        var content = format == ExportFormat.Html ? ToHtml(message, keepRemoteImages) : ToText(message);
        // With a byte order mark: Windows programs otherwise guess the wrong encoding for umlauts.
        await target.WriteAsync(Utf8Bom.GetPreamble(), ct);
        await target.WriteAsync(Utf8Bom.GetBytes(content), ct);
    }

    static IEnumerable<(string Label, string Value)> Header(MimeMessage message)
    {
        yield return ("Von", message.From.ToString());
        yield return ("An", message.To.ToString());
        if (message.Cc.Count > 0) yield return ("Cc", message.Cc.ToString());
        yield return ("Datum", message.Date.ToLocalTime().ToString("dddd, d. MMMM yyyy, HH:mm", German));
        yield return ("Betreff", message.Subject ?? "");
        var attachments = message.Attachments.Select((a, i) => MailRenderer.FileNameOf(a, i)).ToList();
        if (attachments.Count > 0) yield return ("Anhänge", string.Join(", ", attachments));
    }

    public static string ToText(MimeMessage message)
    {
        var mail = MailRenderer.Render(message, new RenderOptions(ShowHtml: false, LoadRemoteImages: false));
        var sb = new StringBuilder();
        foreach (var (label, value) in Header(message)) sb.Append(label).Append(": ").AppendLine(value);
        sb.AppendLine().AppendLine(MailRenderer.ToPlainText(mail.Blocks));
        var links = mail.Links.Where(l => l.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && l.Url != l.Text).ToList();
        if (links.Count > 0)
        {
            sb.AppendLine().AppendLine("Links:");
            foreach (var link in links) sb.AppendLine($"[{link.Index + 1}] {link.Url}");
        }
        return sb.ToString().ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// A self-contained page: the header as a small table, then the mail – cleaned like in the reader (no scripts,
    /// forms or frames), with the images that travel inside the mail embedded.
    /// </summary>
    public static string ToHtml(MimeMessage message, bool keepRemoteImages)
    {
        var mail = MailRenderer.Render(message, new RenderOptions(ShowHtml: true, LoadRemoteImages: keepRemoteImages));
        string body;
        if (mail.Html is not null)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(mail.Html);
            foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
            {
                var source = node.Name == "img" ? "src" : "background";
                var url = HtmlEntity.DeEntitize(node.GetAttributeValue(source, "")).Trim();
                if (url.Length == 0) continue;
                if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    if (mail.InlineImages.TryGetValue(url[4..].Trim('<', '>'), out var bytes))
                        node.SetAttributeValue(source, $"data:{ImageType(bytes)};base64,{Convert.ToBase64String(bytes)}");
                    else node.Attributes.Remove(source);
                }
                else if (!keepRemoteImages && HtmlSanitizer.IsRemote(url))
                {
                    node.Attributes.Remove(source);
                }
            }
            body = doc.DocumentNode.OuterHtml;
            // Background images set by CSS are fetched by a browser just the same.
            if (!keepRemoteImages) body = RemoteCssUrl().Replace(body, "none");
        }
        else
        {
            body = "<pre style=\"white-space: pre-wrap; font: inherit\">" + WebUtility.HtmlEncode(MailRenderer.ToPlainText(mail.Blocks)) + "</pre>";
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"de\"><head><meta charset=\"utf-8\">\n<title>")
            .Append(WebUtility.HtmlEncode(message.Subject ?? "")).Append("</title>\n")
            .Append("<style>body{font-family:Segoe UI,Ubuntu,Cantarell,sans-serif;font-size:10.5pt;color:#1d1f24;margin:24px;")
            .Append(mail.HtmlBackground is { } bg ? $"background-color:{bg};" : "").Append("}")
            .Append(".wisp-header{border-collapse:collapse;margin:0 0 20px 0;padding:12px 16px;background:#fff;color:#1d1f24;border-bottom:1px solid #dfe2e8;width:100%;font-size:10pt}")
            .Append(".wisp-header td{padding:2px 12px 2px 0;vertical-align:top}.wisp-header td:first-child{color:#6a6f7c;white-space:nowrap;width:1%}")
            .Append("</style>\n</head><body>\n<table class=\"wisp-header\">\n");
        foreach (var (label, value) in Header(message))
            sb.Append("<tr><td>").Append(label).Append("</td><td>").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>\n");
        sb.Append("</table>\n").Append(body).Append("\n</body></html>\n");
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"url\(\s*['""]?\s*(https?:)?//[^)]*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RemoteCssUrl();

    static string ImageType(byte[] data) => data switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [0xFF, 0xD8, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..] => "image/webp",
        [(byte)'B', (byte)'M', ..] => "image/bmp",
        _ => "image/png",
    };
}
