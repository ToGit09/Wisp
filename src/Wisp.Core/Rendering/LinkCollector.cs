using System.Text.RegularExpressions;

namespace Wisp.Core.Rendering;

/// <summary>
/// Collects links of a mail and flags the classic phishing pattern: the visible text shows one
/// domain ("meine-bank.de") while the link actually leads somewhere else.
/// </summary>
public sealed partial class LinkCollector
{
    // Second-level labels under which registrations happen (example.co.uk, example.com.au …).
    static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
        { "co", "com", "net", "org", "gov", "ac", "edu", "or", "ne", "go" };

    readonly List<LinkInfo> _links = [];
    readonly Dictionary<string, int> _byUrl = new(StringComparer.Ordinal);

    public IReadOnlyList<LinkInfo> Links => _links;

    /// <summary>Registers a link and returns its index (equal URLs share one entry).</summary>
    public int Add(string url, string text)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (_byUrl.TryGetValue(url, out var existing)) return existing;

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        if (uri?.Scheme == Uri.UriSchemeMailto) host = url[7..].Split('?')[0];
        var info = new LinkInfo(_links.Count, text, url, host, IsSuspicious(text, uri));
        _links.Add(info);
        _byUrl[url] = info.Index;
        return info.Index;
    }

    public static bool IsSuspicious(string text, Uri? target)
    {
        if (target is null) return true;
        if (target.Scheme is not ("http" or "https")) return false;
        var shown = DomainInText().Match(text);
        if (!shown.Success) return false;

        var shownDomain = Registrable(shown.Groups["host"].Value);
        var realDomain = Registrable(target.IdnHost);
        return !string.Equals(shownDomain, realDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Approximates the registrable domain ("login.paypal.com" → "paypal.com").</summary>
    public static string Registrable(string host)
    {
        var labels = host.Trim('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2) return string.Join('.', labels);
        var take = SecondLevel.Contains(labels[^2]) && labels[^1].Length == 2 ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(?<host>(?:[a-z0-9-]+\.)+[a-z]{2,})(?:[/\s:]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DomainInText();
}
