using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Wisp.Core.Rendering;

/// <summary>
/// Rewrites table layouts the HTML renderer gets wrong into equivalent ones it handles. Newsletters are built
/// from nested tables and rely on three things it does not do: centring a fixed-width table, percentage heights
/// and stretching a cell to its row height (so a cell's background only covered its text).
/// </summary>
public static partial class LayoutFixes
{
    public static void Apply(HtmlDocument doc)
    {
        var tables = doc.DocumentNode.Descendants("table").ToList();
        foreach (var node in tables.Concat(doc.DocumentNode.Descendants("td")).ToList()) DropPercentHeight(node);
        foreach (var table in tables) FillRowHeight(table);
        foreach (var table in tables) CenterIfFixed(table);
    }

    // ───────────────────────── Centring ─────────────────────────

    /// <summary>
    /// align="center", margin: auto and a centred parent cell are all ignored by the renderer for tables.
    /// Empty columns left and right of a fixed-width cell are the one thing it centres.
    /// </summary>
    static void CenterIfFixed(HtmlNode table)
    {
        if (PxWidth(table) is not { } width || table.ParentNode is not { } parent) return;
        var centered = table.GetAttributeValue("align", "").Equals("center", StringComparison.OrdinalIgnoreCase)
            || AutoMargins().IsMatch(table.GetAttributeValue("style", ""))
            || IsCentering(parent);
        if (!centered) return;

        var wrapper = HtmlNode.CreateNode(string.Create(CultureInfo.InvariantCulture,
            $"""<table width="100%" cellpadding="0" cellspacing="0" border="0"><tr><td></td><td width="{width}"></td><td></td></tr></table>"""));
        parent.ReplaceChild(wrapper, table);
        wrapper.SelectSingleNode(".//td[2]").AppendChild(table);
    }

    static bool IsCentering(HtmlNode node) =>
        node.Name == "center"
        || node.GetAttributeValue("align", "").Equals("center", StringComparison.OrdinalIgnoreCase)
        || TextAlignCenter().IsMatch(node.GetAttributeValue("style", ""));

    static double? PxWidth(HtmlNode table)
    {
        if (StyleWidth().Match(table.GetAttributeValue("style", "")) is { Success: true } m) return Number(m.Groups[1].Value);
        var attr = table.GetAttributeValue("width", "").Trim();
        if (attr.EndsWith("px", StringComparison.OrdinalIgnoreCase)) attr = attr[..^2];
        return Number(attr);
    }

    // ───────────────────────── Heights ─────────────────────────

    /// <summary>
    /// The renderer resolves height: 100% against the wrong box and makes cells far too tall. Without it the
    /// content gets its natural height; content that sat at the bottom of the full-height box moves down by
    /// aligning the surrounding cell to the bottom instead.
    /// </summary>
    static void DropPercentHeight(HtmlNode node)
    {
        var removed = false;
        if (node.GetAttributeValue("height", "").Trim().EndsWith('%'))
        {
            node.Attributes.Remove("height");
            removed = true;
        }
        var style = node.GetAttributeValue("style", "");
        if (PercentHeight().IsMatch(style))
        {
            node.SetAttributeValue("style", PercentHeight().Replace(style, ""));
            removed = true;
        }
        if (!removed || !AlignsBottom(node)) return;

        var table = node.Name == "table" ? node : node.Ancestors("table").FirstOrDefault();
        if (table?.Ancestors("td").FirstOrDefault() is { } outer && !outer.Attributes.Contains("valign"))
            outer.SetAttributeValue("valign", "bottom");
    }

    static bool AlignsBottom(HtmlNode node) =>
        (node.Name == "table" ? node.Descendants("td").Take(1) : [node]).Any(cell =>
            cell.GetAttributeValue("valign", "").Equals("bottom", StringComparison.OrdinalIgnoreCase)
            || VerticalAlignBottom().IsMatch(cell.GetAttributeValue("style", "")));

    /// <summary>A table with a fixed height and one row: the cells get that height, so their backgrounds fill it.</summary>
    static void FillRowHeight(HtmlNode table)
    {
        if (PxHeight(table) is not { } height) return;
        var rows = table.ChildNodes.Where(n => n.Name is "tbody" or "thead").SelectMany(n => n.ChildNodes)
            .Concat(table.ChildNodes).Where(n => n.Name == "tr").ToList();
        if (rows.Count != 1) return;
        foreach (var cell in rows[0].ChildNodes.Where(n => n.Name is "td" or "th"))
            if (!cell.Attributes.Contains("height") && !StyleHeight().IsMatch(cell.GetAttributeValue("style", "")))
                cell.SetAttributeValue("height", height.ToString(CultureInfo.InvariantCulture));
    }

    static double? PxHeight(HtmlNode node)
    {
        if (StyleHeight().Match(node.GetAttributeValue("style", "")) is { Success: true } m) return Number(m.Groups[1].Value);
        var attr = node.GetAttributeValue("height", "").Trim();
        if (attr.EndsWith("px", StringComparison.OrdinalIgnoreCase)) attr = attr[..^2];
        return Number(attr);
    }

    static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    [GeneratedRegex(@"(?<![-\w])width\s*:\s*([\d.]+)px", RegexOptions.IgnoreCase)]
    private static partial Regex StyleWidth();

    [GeneratedRegex(@"(?<![-\w])height\s*:\s*([\d.]+)px", RegexOptions.IgnoreCase)]
    private static partial Regex StyleHeight();

    [GeneratedRegex(@"(?<![-\w])height\s*:\s*[\d.]+%\s*(!important)?\s*;?", RegexOptions.IgnoreCase)]
    private static partial Regex PercentHeight();

    [GeneratedRegex(@"(?<![-\w])margin(-left|-right)?\s*:[^;]*\bauto\b", RegexOptions.IgnoreCase)]
    private static partial Regex AutoMargins();

    [GeneratedRegex(@"(?<![-\w])text-align\s*:\s*center", RegexOptions.IgnoreCase)]
    private static partial Regex TextAlignCenter();

    [GeneratedRegex(@"(?<![-\w])vertical-align\s*:\s*bottom", RegexOptions.IgnoreCase)]
    private static partial Regex VerticalAlignBottom();
}
