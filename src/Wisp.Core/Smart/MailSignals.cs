using System.Text.RegularExpressions;
using MimeKit;

namespace Wisp.Core.Smart;

/// <summary>
/// Facts from a mail's header that help to sort it. Read once when the header arrives and stored as a
/// small bit mask, so sorting never needs the mail text again.
/// </summary>
[Flags]
public enum MailSignals
{
    None = 0,
    /// <summary>The header was looked at (distinguishes "nothing found" from "not checked yet").</summary>
    Checked = 1,
    /// <summary>List-Unsubscribe / List-Id: a newsletter or mailing.</summary>
    Newsletter = 2,
    /// <summary>Precedence: bulk/list/junk, or a typical mass-mailing service.</summary>
    Bulk = 4,
    /// <summary>Auto-Submitted, noreply sender: sent by a machine (order confirmations, alerts).</summary>
    Automated = 8,
    /// <summary>SPF, DKIM or DMARC failed according to the receiving server.</summary>
    AuthFailed = 16,
    /// <summary>SPF/DKIM/DMARC passed.</summary>
    AuthPassed = 32,
    /// <summary>The receiving server itself flagged it (X-Spam-Flag: YES and the like).</summary>
    ServerSpam = 64,
    /// <summary>Answers go to a different domain than the sender's.</summary>
    ReplyToElsewhere = 128,
    /// <summary>The display name contains an address or domain that is not the real sender.</summary>
    NameSpoof = 256,
    /// <summary>Marked as important by the sender (Importance/X-Priority high).</summary>
    HighPriority = 512,
    /// <summary>Addressed to the recipient directly (not via Bcc or a list).</summary>
    DirectlyAddressed = 1024,
}

public static partial class MailSignalReader
{
    /// <summary>Header fields to fetch together with the envelope. Small, and enough for all signals.</summary>
    public static readonly string[] HeaderNames =
    [
        "List-Unsubscribe", "List-Id", "Precedence", "Auto-Submitted", "Authentication-Results",
        "X-Spam-Flag", "X-Spam-Status", "X-Spam", "Reply-To", "Importance", "X-Priority",
        "X-Mailgun-Sid", "X-SG-EID", "X-MC-User", "X-Campaign", "X-CSA-Complaints", "Feedback-ID",
    ];

    [GeneratedRegex(@"\b(spf|dkim|dmarc)\s*=\s*(pass|fail|softfail|permerror)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AuthRegex();

    [GeneratedRegex(@"[a-z0-9.-]+\.[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    public static MailSignals Read(HeaderList? headers, string fromName, string fromAddress, string to, IEnumerable<string> ownAddresses)
    {
        var s = MailSignals.Checked;
        if (headers is not null)
        {
            if (headers.Contains("List-Unsubscribe") || headers.Contains("List-Id")) s |= MailSignals.Newsletter;
            var precedence = headers["Precedence"]?.Trim().ToLowerInvariant();
            if (precedence is "bulk" or "list" or "junk") s |= MailSignals.Bulk;
            if (headers.Contains("X-Mailgun-Sid") || headers.Contains("X-SG-EID") || headers.Contains("X-MC-User") ||
                headers.Contains("X-Campaign") || headers.Contains("X-CSA-Complaints"))
                s |= MailSignals.Bulk;
            var auto = headers["Auto-Submitted"]?.Trim().ToLowerInvariant();
            if (auto is not null && auto != "no") s |= MailSignals.Automated;

            foreach (var auth in headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (Match m in AuthRegex().Matches(auth.Value))
                {
                    var result = m.Groups[2].Value.ToLowerInvariant();
                    if (result == "pass") s |= MailSignals.AuthPassed;
                    else if (result is "fail" or "permerror" || (result == "softfail" && m.Groups[1].Value.Equals("spf", StringComparison.OrdinalIgnoreCase)))
                        s |= MailSignals.AuthFailed;
                }
            }
            if (headers["X-Spam-Flag"]?.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase) == true ||
                headers["X-Spam-Status"]?.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase) == true ||
                headers["X-Spam"]?.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase) == true)
                s |= MailSignals.ServerSpam;

            if (headers["Reply-To"] is { } replyTo && Domain(ExtractAddress(replyTo)) is { } replyDomain &&
                Domain(fromAddress) is { } fromDomain && !SameOrganisation(replyDomain, fromDomain))
                s |= MailSignals.ReplyToElsewhere;

            var importance = headers["Importance"]?.Trim().ToLowerInvariant();
            var priority = headers["X-Priority"]?.Trim();
            if (importance == "high" || priority is not null && (priority.StartsWith('1') || priority.StartsWith('2')))
                s |= MailSignals.HighPriority;
        }

        var local = fromAddress.Split('@')[0].ToLowerInvariant();
        if (local.Contains("noreply") || local.Contains("no-reply") || local.Contains("donotreply") || local.Contains("do-not-reply") ||
            local is "mailer-daemon" or "notification" or "notifications" or "info" or "news" or "newsletter" or "marketing")
            s |= MailSignals.Automated;

        // "PayPal <service@paypa1-secure.example>" is fine, but "service@paypal.com <x@evil.example>" is not.
        if (Domain(fromAddress) is { } senderDomain)
        {
            foreach (Match m in DomainRegex().Matches(fromName))
            {
                if (!SameOrganisation(m.Value.ToLowerInvariant(), senderDomain))
                {
                    s |= MailSignals.NameSpoof;
                    break;
                }
            }
        }

        var own = ownAddresses.Where(a => a.Length > 0).ToList();
        if (own.Any(a => to.Contains(a, StringComparison.OrdinalIgnoreCase))) s |= MailSignals.DirectlyAddressed;
        return s;
    }

    static string ExtractAddress(string value)
    {
        var lt = value.IndexOf('<');
        var gt = value.IndexOf('>', lt + 1);
        return (lt >= 0 && gt > lt ? value[(lt + 1)..gt] : value).Trim();
    }

    public static string? Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? null : address[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
    }

    /// <summary>"mail.example.com" and "example.com" belong together; so do "news.shop.de" and "shop.de".</summary>
    public static bool SameOrganisation(string a, string b) => BaseDomain(a) == BaseDomain(b);

    static string BaseDomain(string domain)
    {
        var parts = domain.Trim('.').Split('.');
        if (parts.Length <= 2) return domain;
        // co.uk, com.au …: keep three labels.
        var secondLevel = parts[^2];
        var take = secondLevel.Length <= 3 && parts[^1].Length == 2 && secondLevel is "co" or "com" or "org" or "net" or "gov" or "ac" ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }
}
