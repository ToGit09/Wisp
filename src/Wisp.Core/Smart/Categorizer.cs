using System.Text.RegularExpressions;
using Wisp.Core.Storage;

namespace Wisp.Core.Smart;

/// <summary>The tabs above the inbox. "Alle" shows everything; <see cref="None"/> mails appear only there.</summary>
public enum MailCategory
{
    None = 0,
    Important = 1,
    Promotion = 2,
    SuspectedSpam = 3,
}

/// <summary>How a mail was sorted: not yet, by the rules, or by the local AI model.</summary>
public enum ClassifiedBy
{
    Pending = 0,
    Rules = 1,
    Ai = 2,
}

public sealed record ClassifyInput(Envelope Envelope, MailSignals Signals, MessageFlags Flags, bool KnownContact, MailCategory? SenderRule);

public sealed record Classification(MailCategory Category, double SpamScore);

/// <summary>
/// Sorting without AI: header facts, the user's own contacts, sender rules and a word filter that learns
/// from the spam folder. Fast enough for thousands of mails per second.
/// </summary>
public static class Categorizer
{
    /// <summary>From this score on a mail counts as "Spamverdacht".</summary>
    public const double SpamThreshold = 0.5;

    public static Classification Classify(ClassifyInput input, SpamFilter? filter, bool spamDetection = true)
    {
        var s = input.Signals;
        var spam = spamDetection ? SpamScore(input, filter) : 0;
        if (input.SenderRule is { } rule) return new Classification(rule, rule == MailCategory.SuspectedSpam ? 1 : Math.Min(spam, 0.2));

        var bulk = s.HasFlag(MailSignals.Newsletter) || s.HasFlag(MailSignals.Bulk);
        if (input.KnownContact && !bulk) return new Classification(MailCategory.Important, spam);
        if (spam >= SpamThreshold) return new Classification(MailCategory.SuspectedSpam, spam);
        if (bulk) return new Classification(MailCategory.Promotion, spam);
        if (input.KnownContact) return new Classification(MailCategory.Important, spam);

        // A person writing to me directly (not a machine, not a mailing) is worth a look.
        var personal = s.HasFlag(MailSignals.DirectlyAddressed) && !s.HasFlag(MailSignals.Automated);
        if (personal && (s.HasFlag(MailSignals.HighPriority) || spam < 0.25)) return new Classification(MailCategory.Important, spam);
        return new Classification(MailCategory.None, spam);
    }

    /// <summary>0 = certainly wanted, 1 = certainly spam.</summary>
    public static double SpamScore(ClassifyInput input, SpamFilter? filter)
    {
        if (input.KnownContact) return 0;
        var s = input.Signals;
        var score = 0.0;
        if (s.HasFlag(MailSignals.ServerSpam)) score += 0.6;
        if (s.HasFlag(MailSignals.AuthFailed)) score += 0.35;
        if (s.HasFlag(MailSignals.NameSpoof)) score += 0.35;
        if (s.HasFlag(MailSignals.ReplyToElsewhere) && !s.HasFlag(MailSignals.Newsletter)) score += 0.15;
        if (s.HasFlag(MailSignals.AuthPassed) && !s.HasFlag(MailSignals.AuthFailed)) score -= 0.1;
        if (!s.HasFlag(MailSignals.DirectlyAddressed) && !s.HasFlag(MailSignals.Newsletter)) score += 0.1;
        score += SuspiciousText(input.Envelope);

        if (filter is { IsTrained: true })
        {
            var p = filter.Probability(SpamFilter.TextOf(input.Envelope, s));
            score += (p - 0.5) * 0.9;
        }
        return Math.Clamp(score, 0, 1);
    }

    static readonly Regex Shouting = new(@"\b[A-ZÄÖÜ]{6,}\b", RegexOptions.Compiled);
    static readonly Regex Bait = new(
        @"gewinn|gewonnen|lotterie|erbschaft|bitcoin|krypto|crypto|viagra|casino|kredit ohne|sofort geld|million|dringend.*konto|konto.*gesperrt|account.*(suspend|locked)|verify your account|bestätigen sie ihr konto|ihr paket konnte nicht|zahlung fehlgeschlagen|you have won|prince|inheritance",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static double SuspiciousText(Envelope e)
    {
        var score = 0.0;
        if (Shouting.Matches(e.Subject).Count >= 2) score += 0.1;
        if (e.Subject.Count(c => c == '!') >= 3) score += 0.1;
        if (Bait.IsMatch(e.Subject) || Bait.IsMatch(e.Preview)) score += 0.2;
        return score;
    }
}

/// <summary>
/// A small naive-Bayes word filter. Trained in memory from the user's own spam folder and inbox; the word
/// counts are never written to disk, so nothing about the mail content leaks outside the encrypted store.
/// </summary>
public sealed class SpamFilter
{
    const int MinSamples = 15;
    readonly Dictionary<string, (int Spam, int Ham)> _counts = new(StringComparer.Ordinal);
    int _spamDocs, _hamDocs;

    public bool IsTrained => _spamDocs >= MinSamples && _hamDocs >= MinSamples;
    public int SpamSamples => _spamDocs;
    public int HamSamples => _hamDocs;

    public static SpamFilter Train(IEnumerable<(string Text, bool Spam)> samples)
    {
        var filter = new SpamFilter();
        foreach (var (text, spam) in samples) filter.Add(text, spam);
        return filter;
    }

    public void Add(string text, bool spam)
    {
        if (spam) _spamDocs++; else _hamDocs++;
        foreach (var token in Tokens(text).Distinct())
        {
            var (s, h) = _counts.GetValueOrDefault(token);
            _counts[token] = spam ? (s + 1, h) : (s, h + 1);
        }
    }

    /// <summary>Robinson/Fisher-style combination of the 15 most telling words. 0.5 = no opinion.</summary>
    public double Probability(string text)
    {
        if (!IsTrained) return 0.5;
        var probs = new List<double>();
        foreach (var token in Tokens(text).Distinct())
        {
            if (!_counts.TryGetValue(token, out var c)) continue;
            var spamFreq = (double)c.Spam / _spamDocs;
            var hamFreq = (double)c.Ham / _hamDocs;
            var p = spamFreq / (spamFreq + hamFreq);
            var n = c.Spam + c.Ham;
            // Rare words are pulled towards 0.5 (strength 1, assumed 0.5).
            probs.Add((1 * 0.5 + n * p) / (1 + n));
        }
        if (probs.Count == 0) return 0.5;
        var telling = probs.OrderByDescending(p => Math.Abs(p - 0.5)).Take(15).ToList();
        double logSpam = 0, logHam = 0;
        foreach (var p in telling)
        {
            var clamped = Math.Clamp(p, 0.01, 0.99);
            logSpam += Math.Log(clamped);
            logHam += Math.Log(1 - clamped);
        }
        return 1 / (1 + Math.Exp(logHam - logSpam));
    }

    public static string TextOf(Envelope e, MailSignals s)
    {
        var domain = MailSignalReader.Domain(e.FromAddress) ?? "";
        var tags = string.Join(' ', Enum.GetValues<MailSignals>()
            .Where(f => f is not (MailSignals.None or MailSignals.Checked) && s.HasFlag(f))
            .Select(f => "sig_" + f.ToString().ToLowerInvariant()));
        return $"{e.Subject} {e.FromName} dom_{domain} {e.Preview} {tags}";
    }

    static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var part = char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '$' or '€';
            if (part && start < 0) start = i;
            else if (!part && start >= 0)
            {
                var token = text[start..i].Trim('.', '-').ToLowerInvariant();
                if (token.Length is >= 3 and <= 30) yield return token;
                start = -1;
            }
        }
    }
}
