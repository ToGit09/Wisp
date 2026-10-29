using System.Globalization;
using System.Text;
using MimeKit;
using MimeKit.Utils;
using Wisp.Core.Accounts;
using Wisp.Core.Rendering;

namespace Wisp.Core.Mail;

public sealed record Draft
{
    public long AccountId { get; init; }
    public string To { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Bcc { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>Source from the Advanced editor (see <see cref="HtmlEditing"/>). Null for a plain text mail.</summary>
    public string? HtmlSource { get; init; }
    public IReadOnlyList<string> AttachmentPaths { get; init; } = [];
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    /// <summary>Local id of the message being answered (gets the "answered" flag after sending).</summary>
    public long? RepliesTo { get; init; }
    /// <summary>Local id of the draft this mail was written from; it goes to the trash once the mail is sent.</summary>
    public long? ReplacesDraft { get; init; }
    /// <summary>Local id of a message whose attachments are forwarded.</summary>
    public long? ForwardsAttachmentsOf { get; init; }
}

public static class Composer
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Validates addresses. Returns a German error message, or null if the draft can be sent.</summary>
    public static string? Validate(Draft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.To) && string.IsNullOrWhiteSpace(draft.Cc) && string.IsNullOrWhiteSpace(draft.Bcc))
            return "Bitte mindestens einen Empfänger angeben.";
        foreach (var (label, value) in new[] { ("An", draft.To), ("Cc", draft.Cc), ("Bcc", draft.Bcc) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!InternetAddressList.TryParse(Normalize(value), out var list) || list.Mailboxes.Any(m => !m.Address.Contains('@')))
                return $"Die Adresse im Feld „{label}“ ist ungültig.";
        }
        foreach (var path in draft.AttachmentPaths)
            if (!File.Exists(path)) return $"Der Anhang „{Path.GetFileName(path)}“ wurde nicht gefunden.";
        return null;
    }

    public static MimeMessage Build(Draft draft, AccountConfig account, MimeMessage? forwardSource = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        AddAll(message.To, draft.To);
        AddAll(message.Cc, draft.Cc);
        AddAll(message.Bcc, draft.Bcc);
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;
        message.MessageId = MimeUtils.GenerateMessageId(ProviderPresets.DomainOf(account.Email) ?? "wisp.local");
        if (draft.InReplyTo is { Length: > 0 } inReplyTo) message.InReplyTo = inReplyTo;
        foreach (var reference in draft.References) message.References.Add(reference);

        var builder = new BodyBuilder { TextBody = draft.Body };
        if (draft.HtmlSource is { } source)
        {
            // multipart/alternative: recipients without HTML get a readable text version.
            var html = ToMailHtml(source);
            builder.HtmlBody = html;
            builder.TextBody = HtmlEditing.HtmlToText(html);
        }
        foreach (var path in draft.AttachmentPaths) builder.Attachments.Add(path);
        if (forwardSource is not null)
        {
            foreach (var attachment in forwardSource.Attachments)
                builder.Attachments.Add(attachment);
        }
        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>
    /// The HTML that is actually sent: the user's source with line breaks applied, cleaned of anything
    /// active (pasted scripts, forms …) and wrapped in a minimal, widely compatible document.
    /// </summary>
    public static string ToMailHtml(string source)
    {
        var body = HtmlSanitizer.Sanitize(HtmlEditing.SourceToHtml(source)).Html;
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
               "<body style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.5;color:#1d1f24\">" +
               body + "</body></html>";
    }

    /// <summary>
    /// Puts the signature where the user starts typing: above a quoted original, below an empty line.
    /// Uses the standard "-- " separator so mail programs can recognise and strip it in replies.
    /// </summary>
    public static string WithSignature(string body, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return body;
        return "\n\n-- \n" + signature.ReplaceLineEndings("\n").TrimEnd() + body;
    }

    public static Draft Reply(RenderedMail mail, long accountId, string ownAddress, bool all, long messageId, bool quote = true)
    {
        var reply = mail.Reply;
        var to = reply.ReplyTo.Count > 0 ? string.Join(", ", reply.ReplyTo) : Format(reply.FromName, reply.FromAddress);
        var cc = "";
        if (all)
        {
            var others = reply.To.Concat(reply.Cc)
                .Where(a => !a.Contains(ownAddress, StringComparison.OrdinalIgnoreCase)
                            && !a.Contains(reply.FromAddress, StringComparison.OrdinalIgnoreCase));
            cc = string.Join(", ", others);
        }

        var references = reply.References.ToList();
        if (reply.MessageId is { } mid) references.Add(mid);

        return new Draft
        {
            AccountId = accountId,
            To = to,
            Cc = cc,
            Subject = Prefixed("Re: ", reply.Subject, "re:", "aw:"),
            Body = !quote ? "" : $"\n\nAm {reply.Date.ToLocalTime().ToString("dd.MM.yyyy 'um' HH:mm", German)} schrieb {(reply.FromName.Length > 0 ? reply.FromName : reply.FromAddress)}:\n"
                   + Quote(reply.QuotableText),
            InReplyTo = reply.MessageId,
            References = references,
            RepliesTo = messageId,
        };
    }

    public static Draft Forward(RenderedMail mail, long accountId, long messageId)
    {
        var reply = mail.Reply;
        var header = new StringBuilder()
            .AppendLine()
            .AppendLine()
            .AppendLine("-------- Weitergeleitete Nachricht --------")
            .AppendLine($"Von: {mail.From}")
            .AppendLine($"Datum: {reply.Date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German)}")
            .AppendLine($"Betreff: {reply.Subject}")
            .AppendLine($"An: {mail.To}");
        if (mail.Cc is { } cc) header.AppendLine($"Cc: {cc}");
        header.AppendLine().Append(reply.QuotableText);

        return new Draft
        {
            AccountId = accountId,
            Subject = Prefixed("Fwd: ", reply.Subject, "fwd:", "fw:", "wg:"),
            Body = header.ToString(),
            ForwardsAttachmentsOf = mail.Attachments.Count > 0 ? messageId : null,
        };
    }

    static string Prefixed(string prefix, string subject, params string[] existing) =>
        existing.Any(p => subject.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? subject : prefix + subject;

    static string Quote(string text) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(l => l.StartsWith('>') ? ">" + l : "> " + l));

    static string Format(string name, string address) =>
        string.IsNullOrWhiteSpace(name) ? address : new MailboxAddress(name, address).ToString();

    // Users often separate addresses with ";" (Outlook habit).
    static string Normalize(string value) => value.Replace(';', ',');

    static void AddAll(InternetAddressList list, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.AddRange(InternetAddressList.Parse(Normalize(value)));
    }
}
