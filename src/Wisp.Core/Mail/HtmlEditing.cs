using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Wisp.Core.Rendering;

namespace Wisp.Core.Mail;

/// <summary>The edited text plus where the selection should be afterwards.</summary>
public sealed record EditResult(string Text, int SelectionStart, int SelectionEnd);

/// <summary>
/// Helpers for the "Advanced" composer. The user edits HTML source in which line breaks count
/// (like in a normal text box); <see cref="SourceToHtml"/> turns that into real HTML.
/// The formatting buttons only insert tags around the selection, so nothing is hidden from the user.
/// </summary>
public static partial class HtmlEditing
{
    // ───────────────────────── conversions ─────────────────────────

    /// <summary>Plain text → editable source (special characters escaped, line breaks kept).</summary>
    public static string TextToSource(string text) =>
        WebUtility.HtmlEncode(text.ReplaceLineEndings("\n")).Replace("&#39;", "'");

    /// <summary>
    /// Editable source → HTML. Each line break becomes &lt;br&gt;, except next to block elements
    /// (paragraphs, lists, headings, quotes …) where it would add unwanted empty lines.
    /// </summary>
    public static string SourceToHtml(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var html = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            html.Append(lines[i]);
            if (i == lines.Length - 1) break;
            var endsWithBlock = BlockAtEnd().IsMatch(lines[i].TrimEnd());
            var nextStartsWithBlock = BlockAtStart().IsMatch(lines[i + 1].TrimStart());
            html.Append(endsWithBlock || nextStartsWithBlock ? "\n" : "<br>\n");
        }
        return html.ToString();
    }

    /// <summary>HTML → readable plain text (for the text part of the mail and for switching back to Light).</summary>
    public static string HtmlToText(string html) =>
        MailRenderer.ToPlainText(MailRenderer.FromHtml(html, new LinkCollector()));

    /// <summary>True if the source uses any tag, i.e. switching to Light would lose formatting.</summary>
    public static bool HasMarkup(string source) => AnyTag().IsMatch(source);

    // ───────────────────────── formatting buttons ─────────────────────────

    /// <summary>Wraps the selection in an inline tag, e.g. &lt;b&gt;…&lt;/b&gt;. Without selection the caret lands between the tags.</summary>
    public static EditResult WrapInline(string text, int start, int end, string open, string close)
    {
        (start, end) = Order(text, start, end);
        var inner = text[start..end];
        var result = text[..start] + open + inner + close + text[end..];
        var innerStart = start + open.Length;
        return new EditResult(result, innerStart, innerStart + inner.Length);
    }

    public static EditResult Bold(string text, int start, int end) => WrapInline(text, start, end, "<b>", "</b>");
    public static EditResult Italic(string text, int start, int end) => WrapInline(text, start, end, "<i>", "</i>");
    public static EditResult Underline(string text, int start, int end) => WrapInline(text, start, end, "<u>", "</u>");

    public static EditResult Color(string text, int start, int end, string hex)
    {
        if (!HexColor().IsMatch(hex)) throw new ArgumentException("Invalid colour.", nameof(hex));
        return WrapInline(text, start, end, $"<span style=\"color:{hex}\">", "</span>");
    }

    /// <summary>Puts the selected lines into a block element (heading, quote) on lines of their own.</summary>
    public static EditResult WrapBlock(string text, int start, int end, string tag)
    {
        (start, end) = ExpandToLines(text, start, end);
        var inner = text[start..end];
        var open = $"<{tag}>";
        var result = text[..start] + open + inner + $"</{tag}>" + text[end..];
        return new EditResult(result, start + open.Length, start + open.Length + inner.Length);
    }

    /// <summary>Turns each selected line into a list item.</summary>
    public static EditResult MakeList(string text, int start, int end, bool numbered)
    {
        (start, end) = ExpandToLines(text, start, end);
        var tag = numbered ? "ol" : "ul";
        var lines = text[start..end].Split('\n');
        var items = string.Join("\n", lines.Select(l => $"  <li>{l.Trim()}</li>"));
        var block = $"<{tag}>\n{items}\n</{tag}>";
        var result = text[..start] + block + text[end..];
        // Empty line: put the caret into the first item so the user can type right away.
        var caret = lines.Length == 1 && lines[0].Trim().Length == 0 ? start + $"<{tag}>\n  <li>".Length : start + block.Length;
        return new EditResult(result, caret, caret);
    }

    /// <summary>Makes the selection a link. Without selection the address itself is the link text.</summary>
    public static EditResult Link(string text, int start, int end, string url)
    {
        url = NormalizeUrl(url) ?? throw new ArgumentException("Invalid address.", nameof(url));
        (start, end) = Order(text, start, end);
        var label = end > start ? text[start..end] : WebUtility.HtmlEncode(url);
        var open = $"<a href=\"{WebUtility.HtmlEncode(url)}\">";
        var result = text[..start] + open + label + "</a>" + text[end..];
        return new EditResult(result, start + open.Length, start + open.Length + label.Length);
    }

    /// <summary>Accepts web and mail addresses only; adds https:// when the scheme is missing.</summary>
    public static string? NormalizeUrl(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (url.Contains('@') && !url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            url = "mailto:" + url;
        else if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? url : null;
    }

    static (int Start, int End) Order(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        return start <= end ? (start, end) : (end, start);
    }

    static (int Start, int End) ExpandToLines(string text, int start, int end)
    {
        (start, end) = Order(text, start, end);
        while (start > 0 && text[start - 1] != '\n') start--;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end);
    }

    const string Blocks = "p|div|ul|ol|li|h[1-6]|blockquote|table|thead|tbody|tr|td|th|hr|pre|br";

    [GeneratedRegex($@"^</?({Blocks})\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockAtStart();

    [GeneratedRegex($@"</?({Blocks})\b[^>]*>$", RegexOptions.IgnoreCase)]
    private static partial Regex BlockAtEnd();

    [GeneratedRegex(@"<[a-zA-Z/][^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
