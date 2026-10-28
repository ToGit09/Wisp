using System.Xml.Linq;

namespace Wisp.Core.Accounts;

public sealed record DiscoveredSettings(ServerSettings Imap, ServerSettings Smtp, string UserName, string? ProviderName);

/// <summary>
/// Finds server settings for domains that are not built in. Only the domain is sent anywhere,
/// never the full address. Results without encryption are discarded.
/// </summary>
public static class AutoConfig
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<DiscoveredSettings?> DiscoverAsync(string email, CancellationToken ct = default)
    {
        var domain = ProviderPresets.DomainOf(email);
        if (domain is null || !Uri.CheckHostName(domain).Equals(UriHostNameType.Dns)) return null;

        string[] urls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml",
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
        ];
        foreach (var url in urls)
        {
            try
            {
                var xml = await Http.GetStringAsync(url, ct);
                var parsed = Parse(xml, email);
                if (parsed is not null) return parsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                // try the next source
            }
        }
        return null;
    }

    internal static DiscoveredSettings? Parse(string xml, string email)
    {
        var doc = XDocument.Parse(xml);
        var provider = doc.Root?.Element("emailProvider");
        if (provider is null) return null;

        var imap = provider.Elements("incomingServer")
            .Where(e => (string?)e.Attribute("type") == "imap")
            .Select(ToServer).FirstOrDefault(s => s is not null);
        var smtp = provider.Elements("outgoingServer")
            .Where(e => (string?)e.Attribute("type") == "smtp")
            .Select(ToServer).FirstOrDefault(s => s is not null);
        if (imap is null || smtp is null) return null;

        var userTemplate = provider.Elements("incomingServer")
            .FirstOrDefault(e => (string?)e.Attribute("type") == "imap")?.Element("username")?.Value ?? "%EMAILADDRESS%";
        var localPart = email[..email.LastIndexOf('@')];
        var userName = userTemplate
            .Replace("%EMAILADDRESS%", email, StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", localPart, StringComparison.Ordinal)
            .Replace("%EMAILDOMAIN%", ProviderPresets.DomainOf(email), StringComparison.Ordinal);

        return new DiscoveredSettings(imap, smtp, userName, provider.Element("displayName")?.Value);
    }

    static ServerSettings? ToServer(XElement e)
    {
        var host = e.Element("hostname")?.Value;
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(e.Element("port")?.Value, out var port)) return null;
        Security? security = e.Element("socketType")?.Value switch
        {
            "SSL" => Security.Tls,
            "STARTTLS" => Security.StartTls,
            _ => null, // "plain" is rejected on purpose
        };
        return security is null ? null : new ServerSettings(host, port, security.Value);
    }
}
