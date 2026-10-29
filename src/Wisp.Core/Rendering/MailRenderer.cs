using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MimeKit;

namespace Wisp.Core.Rendering;

/// <summary>Turns a raw MIME message into something the UI can show safely.</summary>
public static partial class MailRenderer
{
    public static MimeMessage Parse(byte[] raw) => MimeMessage.Load(new MemoryStream(raw));

    public static RenderedMail Render(byte[] raw, RenderOptions options) => Render(Parse(raw), options);

    public static RenderedMail Render(MimeMessage message, RenderOptions options)
    {
        var links = new LinkCollector();
        var htmlBody = message.HtmlBody;
        var textBody = message.TextBody;

        // Plain text: prefer the sender's own text version, fall back to converting the HTML.
        IReadOnlyList<ContentBlock> blocks = textBody is not null
            ? FromPlainText(textBody, links)
            : htmlBody is not null ? FromHtml(htmlBody, links) : [];

        string? html = null;
        var remoteImages = false;
        if (htmlBody is not null)
        {
            var sanitized = HtmlSanitizer.Sanitize(htmlBody);
            remoteImages = sanitized.HasRemoteImages;
            if (options.ShowHtml)
            {
                html = sanitized.Html;
                if (textBody is not null) // collect links from the HTML too, for the phishing check
                    FromHtml(htmlBody, links);
            }
        }

        var inline = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.ContentId is { Length: > 0 } cid && part.ContentType.MediaType == "image" && part.Content is not null)
            {
                using var buffer = new MemoryStream();
                part.Content.DecodeTo(buffer);
                inline[cid.Trim('<', '>')] = buffer.ToArray();
            }
        }

        var attachments = message.Attachments
            .Select((a, i) => new AttachmentInfo(i, FileNameOf(a, i), a.ContentType.MimeType, SizeOf(a)))
            .ToList();

        var from = message.From.Mailboxes.FirstOrDefault();
        return new RenderedMail
        {
            Subject = message.Subject ?? "",
            From = from is null ? "" : string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>",
            To = message.To.ToString(),
            Cc = message.Cc.Count > 0 ? message.Cc.ToString() : null,
            Date = message.Date,
            Blocks = blocks,
            Html = html,
            InlineImages = inline,
            Links = links.Links,
            Attachments = attachments,
            HasHtml = htmlBody is not null,
            HasRemoteImages = remoteImages,
            RemoteImagesAllowed = options.LoadRemoteImages,
            Reply = new ReplyContext(
                message.MessageId,
                message.References.ToList(),
                from?.Address ?? "", from?.Name ?? "",
                message.ReplyTo.Mailboxes.Select(m => m.ToString()).ToList(),
                message.To.Mailboxes.Select(m => m.ToString()).ToList(),
                message.Cc.Mailboxes.Select(m => m.ToString()).ToList(),
                message.Subject ?? "", message.Date,
                textBody ?? (htmlBody is null ? "" : ToPlainText(FromHtml(htmlBody, new LinkCollector())))),
        };
    }

    public static string FileNameOf(MimeEntity entity, int index) =>
        entity is MimePart { FileName: { Length: > 0 } name } ? SafeFileName(name)
        : entity is MessagePart ? $"Nachricht-{index + 1}.eml"
        : $"Anhang-{index + 1}";

    /// <summary>Removes path components and characters that are invalid on Windows or Linux.</summary>
    public static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "Anhang" : clean;
    }

    static long SizeOf(MimeEntity entity) => entity is MimePart { Content.Stream.CanSeek: true } part ? part.Content.Stream.Length : 0;

    public static string ToPlainText(IEnumerable<ContentBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            var text = string.Concat(block.Spans.Select(s => s.Text));
            sb.AppendLine(block.Kind switch
            {
                BlockKind.Quote => "> " + text,
                BlockKind.ListItem => "• " + text,
                BlockKind.Separator => "———",
                _ => text,
            });
            if (block.Kind is BlockKind.Paragraph or BlockKind.Heading) sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── plain text ─────────────────────────

    internal static IReadOnlyList<ContentBlock> FromPlainText(string text, LinkCollector links)
    {
        var blocks = new List<ContentBlock>();
        var paragraph = new StringBuilder();
        var quote = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new ContentBlock(BlockKind.Paragraph, Linkify(paragraph.ToString().TrimEnd(), links)));
                paragraph.Clear();
            }
            if (quote.Length > 0)
            {
                blocks.Add(new ContentBlock(BlockKind.Quote, Linkify(quote.ToString().TrimEnd(), links)));
                quote.Clear();
            }
        }

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith('>'))
            {
                if (paragraph.Length > 0) Flush();
                quote.AppendLine(line.TrimStart('>', ' '));
            }
            else if (line.Length == 0)
            {
                Flush();
            }
            else
            {
                if (quote.Length > 0) Flush();
                paragraph.AppendLine(line);
            }
        }
        Flush();
        return blocks;
    }

    static List<TextSpan> Linkify(string text, LinkCollector links)
    {
        var spans = new List<TextSpan>();
        var last = 0;
        foreach (Match m in PlainUrl().Matches(text))
        {
            if (m.Index > last) spans.Add(new TextSpan(text[last..m.Index]));
            var url = m.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?');
            spans.Add(new TextSpan(url, LinkIndex: links.Add(url, url)));
            if (url.Length < m.Value.Length) spans.Add(new TextSpan(m.Value[url.Length..]));
            last = m.Index + m.Length;
        }
        if (last < text.Length) spans.Add(new TextSpan(text[last..]));
        return spans;
    }

    // ───────────────────────── HTML → readable text ─────────────────────────

    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "head", "title", "noscript", "template", "svg", "iframe", "object", "form", "button", "select" };

    static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "table", "tr", "center", "address", "dd", "dt", "figure", "figcaption" };

    internal static IReadOnlyList<ContentBlock> FromHtml(string html, LinkCollector links)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var builder = new BlockBuilder();
        Walk(doc.DocumentNode, builder, links, bold: false, italic: false, link: -1, pre: false);
        builder.End(BlockKind.Paragraph);
        return builder.Blocks;
    }

    static void Walk(HtmlNode node, BlockBuilder b, LinkCollector links, bool bold, bool italic, int link, bool pre)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    var text = HtmlEntity.DeEntitize(child.InnerText);
                    if (!pre) text = Whitespace().Replace(text, " ");
                    b.Append(text, bold, italic, link);
                    continue;
                case HtmlNodeType.Element:
                    break;
                default:
                    continue;
            }

            var name = child.Name.ToLowerInvariant();
            if (Skip.Contains(name)) continue;

            switch (name)
            {
                case "br":
                    b.LineBreak();
                    break;
                case "hr":
                    b.End(BlockKind.Paragraph);
                    b.Add(new ContentBlock(BlockKind.Separator, []));
                    break;
                case "img":
                    var alt = child.GetAttributeValue("alt", "").Trim();
                    if (alt.Length > 0 && alt.Length < 80) b.Append($"[{HtmlEntity.DeEntitize(alt)}]", bold, italic, link);
                    break;
                case "a":
                    var href = HtmlEntity.DeEntitize(child.GetAttributeValue("href", "")).Trim();
                    var index = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? links.Add(href, HtmlEntity.DeEntitize(child.InnerText).Trim())
                        : -1;
                    Walk(child, b, links, bold, italic, index, pre);
                    break;
                case "b" or "strong":
                    Walk(child, b, links, true, italic, link, pre);
                    break;
                case "i" or "em":
                    Walk(child, b, links, bold, true, link, pre);
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    b.End(BlockKind.Paragraph);
                    Walk(child, b, links, true, italic, link, pre);
                    b.End(BlockKind.Heading);
                    break;
                case "li":
                    b.End(BlockKind.Paragraph);
                    Walk(child, b, links, bold, italic, link, pre);
                    b.End(BlockKind.ListItem);
                    break;
                case "blockquote":
                    b.End(BlockKind.Paragraph);
                    Walk(child, b, links, bold, italic, link, pre);
                    b.End(BlockKind.Quote);
                    break;
                case "pre":
                    b.End(BlockKind.Paragraph);
                    Walk(child, b, links, bold, italic, link, true);
                    b.End(BlockKind.Preformatted);
                    break;
                case "td" or "th":
                    Walk(child, b, links, bold || name == "th", italic, link, pre);
                    b.Append(" ", bold, italic, -1);
                    break;
                default:
                    var isBlock = BlockElements.Contains(name);
                    if (isBlock) b.End(BlockKind.Paragraph);
                    Walk(child, b, links, bold, italic, link, pre);
                    if (isBlock) b.End(BlockKind.Paragraph);
                    break;
            }
        }
    }

    sealed class BlockBuilder
    {
        public List<ContentBlock> Blocks { get; } = [];
        List<TextSpan> _spans = [];

        public void Append(string text, bool bold, bool italic, int link)
        {
            if (text.Length == 0) return;
            if (_spans.Count == 0) text = text.TrimStart();
            if (text.Length == 0) return;
            if (_spans.Count > 0 && _spans[^1] is var last && last.Bold == bold && last.Italic == italic && last.LinkIndex == link)
                _spans[^1] = last with { Text = last.Text + text };
            else
                _spans.Add(new TextSpan(text, bold, italic, link));
        }

        public void LineBreak()
        {
            if (_spans.Count > 0) _spans.Add(new TextSpan("\n"));
        }

        public void Add(ContentBlock block) => Blocks.Add(block);

        public void End(BlockKind kind)
        {
            // Trim trailing whitespace and drop empty blocks (layout tables produce many).
            while (_spans.Count > 0 && string.IsNullOrWhiteSpace(_spans[^1].Text)) _spans.RemoveAt(_spans.Count - 1);
            if (_spans.Count > 0)
            {
                _spans[^1] = _spans[^1] with { Text = _spans[^1].Text.TrimEnd() };
                Blocks.Add(new ContentBlock(kind, _spans));
            }
            _spans = [];
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b(https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex PlainUrl();
}
