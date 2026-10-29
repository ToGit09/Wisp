namespace Wisp.Core.Rendering;

public enum BlockKind { Paragraph, Heading, Quote, ListItem, Preformatted, Separator }

public sealed record TextSpan(string Text, bool Bold = false, bool Italic = false, int LinkIndex = -1);

public sealed record ContentBlock(BlockKind Kind, IReadOnlyList<TextSpan> Spans);

public sealed record LinkInfo(int Index, string Text, string Url, string Host, bool Suspicious);

public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size);

/// <summary>Everything needed to answer or forward a message.</summary>
public sealed record ReplyContext(
    string? MessageId, IReadOnlyList<string> References,
    string FromAddress, string FromName, IReadOnlyList<string> ReplyTo,
    IReadOnlyList<string> To, IReadOnlyList<string> Cc,
    string Subject, DateTimeOffset Date, string QuotableText);

public sealed record RenderOptions(bool ShowHtml, bool LoadRemoteImages);

public sealed record RenderedMail
{
    public required string Subject { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public string? Cc { get; init; }
    public DateTimeOffset Date { get; init; }

    /// <summary>Readable text version. Always present, used when HTML display is off.</summary>
    public required IReadOnlyList<ContentBlock> Blocks { get; init; }

    /// <summary>Sanitized HTML (no scripts, forms, frames or event handlers). Only set when HTML display is on.</summary>
    public string? Html { get; init; }

    /// <summary>Images embedded in the mail itself (cid:), safe to show — loading them contacts nobody.</summary>
    public IReadOnlyDictionary<string, byte[]> InlineImages { get; init; } = new Dictionary<string, byte[]>();

    public required IReadOnlyList<LinkInfo> Links { get; init; }
    public required IReadOnlyList<AttachmentInfo> Attachments { get; init; }

    public bool HasHtml { get; init; }
    /// <summary>The mail references images on the internet (potential read tracking).</summary>
    public bool HasRemoteImages { get; init; }
    public bool RemoteImagesAllowed { get; init; }

    public required ReplyContext Reply { get; init; }
}
