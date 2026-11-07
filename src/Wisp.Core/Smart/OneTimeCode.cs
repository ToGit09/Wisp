using System.Text.RegularExpressions;

namespace Wisp.Core.Smart;

/// <summary>
/// Finds a one-time code (2FA, verification, TAN) in a mail. Deliberately strict: a wrong guess would
/// overwrite the clipboard, so a number only counts when a code word stands close to it and nothing
/// suggests an order number, a voucher or a price.
/// </summary>
public static partial class OneTimeCode
{
    /// <summary>How close (in characters) a code word has to be to the number.</summary>
    const int Window = 80;

    // "Bestätigungscode", "Ihr Code", "OTP", "verification code", "TAN", "Einmalpasswort", "2FA" …
    [GeneratedRegex(@"(einmal|bestätigungs|bestaetigungs|sicherheits|verifizierungs|verifikations|anmelde|login|zugangs|authentifizierungs|aktivierungs|freigabe|best\w*)?(code|kennwort|passwort|pin|tan)\b|\botp\b|verif\w*|passcode|one[- ]time|\b2fa\b|two[- ]factor|zwei[- ]faktor|einmal\w*|sign[- ]in|log[- ]?in|anmeld\w*|authenti\w*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordRegex();

    // Things that look like codes but are not: vouchers, order and customer numbers, parcels, invoices.
    [GeneratedRegex(@"rabatt|gutschein|coupon|promo|discount|voucher|aktionscode|bestell|order|kunden|customer|rechnung|invoice|sendung|tracking|paket|parcel|plz|postleitzahl|konto|iban|telefon|phone|tel\.|fax|artikel|item|ticket|referenz|reference|vertrag|contract|mitglied|member",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegativeRegex();

    // 4–8 digits, "123 456" / "123-456", Google style "G-123456". Not part of a longer number, word, date, price or URL.
    [GeneratedRegex(@"(?<![\w.,/:+#€$-])(?:G-)?(\d{3}[ -]\d{3}|\d{4}[ -]\d{4}|\d{4,8})(?![\w,/%€$]|\.\d|-\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidateRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The code to copy, or null when the mail does not clearly contain one.</summary>
    public static string? Find(string? subject, string? text)
    {
        foreach (var source in new[] { subject, text })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (FindIn(Whitespace().Replace(source, " ")) is { } code) return code;
        }
        return null;
    }

    static string? FindIn(string text)
    {
        var keywords = KeywordRegex().Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        if (keywords.Count == 0) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (Match match in CandidateRegex().Matches(text))
        {
            var digits = match.Groups[1].Value.Replace(" ", "").Replace("-", "");
            if (!LooksLikeCode(digits, text, match)) continue;

            var distance = keywords.Min(k => k.End <= match.Index ? match.Index - k.End
                : k.Start >= match.Index + match.Length ? k.Start - (match.Index + match.Length)
                : 0);
            if (distance > Window) continue;

            // Only the text right around the number decides whether it is a voucher or order number.
            var from = Math.Max(0, match.Index - 40);
            var around = text[from..Math.Min(text.Length, match.Index + match.Length + 25)];
            if (NegativeRegex().IsMatch(around)) continue;

            if (distance < bestDistance || (distance == bestDistance && digits.Length == 6))
            {
                best = digits;
                bestDistance = distance;
            }
        }
        return best;
    }

    static bool LooksLikeCode(string digits, string text, Match match)
    {
        if (digits.Length is < 4 or > 8) return false;
        // A year ("2026") or a time ("14:30") is not a code.
        if (digits.Length == 4 && digits is ['1', '9', _, _] or ['2', '0', _, _]) return false;
        if (match.Index > 0 && text[match.Index - 1] == ':' ) return false;
        var after = match.Index + match.Length;
        if (after < text.Length - 1 && text[after] == ':' && char.IsDigit(text[after + 1])) return false;
        // Money: "4500 EUR", "EUR 4500".
        var tail = text[after..Math.Min(text.Length, after + 5)].TrimStart().ToUpperInvariant();
        if (tail.StartsWith("EUR") || tail.StartsWith("USD") || tail.StartsWith("CHF")) return false;
        var head = text[Math.Max(0, match.Index - 5)..match.Index].TrimEnd().ToUpperInvariant();
        if (head.EndsWith("EUR") || head.EndsWith("USD") || head.EndsWith("CHF")) return false;
        // The same digit six times or 123456 are placeholders in templates, not codes.
        return digits.Distinct().Count() > 1;
    }
}
