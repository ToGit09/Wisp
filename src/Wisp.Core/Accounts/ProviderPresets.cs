namespace Wisp.Core.Accounts;

public static class ProviderPresets
{
    static ServerSettings Tls(string host, int port = 993) => new(host, port, Security.Tls);
    static ServerSettings StartTls(string host, int port = 587) => new(host, port, Security.StartTls);

    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        new()
        {
            Id = "gmail", Name = "Gmail",
            Domains = ["gmail.com", "googlemail.com"],
            Imap = Tls("imap.gmail.com"), Smtp = Tls("smtp.gmail.com", 465),
            Auth = AuthKind.Password, AlternativeAuth = AuthKind.OAuthGoogle,
            ServerSavesSent = true,
            PasswordLabel = "App-Passwort",
            Hint = "Google erlaubt Ihr normales Passwort hier nicht. Aktivieren Sie die Bestätigung in zwei Schritten und erstellen Sie ein App-Passwort (16 Zeichen).",
            HelpUrl = "https://myaccount.google.com/apppasswords",
        },
        new()
        {
            Id = "outlook", Name = "Outlook.com",
            Domains = ["outlook.com", "outlook.de", "hotmail.com", "hotmail.de", "live.com", "live.de", "msn.com"],
            Imap = Tls("outlook.office365.com"), Smtp = StartTls("smtp-mail.outlook.com"),
            Auth = AuthKind.OAuthMicrosoft,
            ServerSavesSent = true,
            Hint = "Sie melden sich direkt bei Microsoft im Browser an. Wisp sieht Ihr Passwort nie.",
        },
        new()
        {
            Id = "yahoo", Name = "Yahoo Mail",
            Domains = ["yahoo.com", "yahoo.de", "ymail.com", "rocketmail.com"],
            Imap = Tls("imap.mail.yahoo.com"), Smtp = Tls("smtp.mail.yahoo.com", 465),
            PasswordLabel = "App-Passwort",
            Hint = "Erstellen Sie in den Yahoo-Kontoeinstellungen unter „Kontosicherheit“ ein App-Passwort.",
            HelpUrl = "https://login.yahoo.com/account/security",
        },
        new()
        {
            Id = "icloud", Name = "iCloud Mail",
            Domains = ["icloud.com", "me.com", "mac.com"],
            Imap = Tls("imap.mail.me.com"), Smtp = StartTls("smtp.mail.me.com"),
            PasswordLabel = "App-spezifisches Passwort",
            Hint = "Erstellen Sie auf account.apple.com unter „Anmeldung und Sicherheit“ ein app-spezifisches Passwort.",
            HelpUrl = "https://account.apple.com/account/manage",
        },
        new()
        {
            Id = "gmx", Name = "GMX",
            Domains = ["gmx.de", "gmx.net", "gmx.at", "gmx.ch"],
            Imap = Tls("imap.gmx.net"), Smtp = StartTls("mail.gmx.net"),
            Hint = "Aktivieren Sie einmalig in den GMX-Einstellungen unter „POP3 & IMAP“ den Abruf per IMAP.",
            HelpUrl = "https://hilfe.gmx.net/pop-imap/einschalten.html",
        },
        new()
        {
            Id = "gmx-com", Name = "GMX (international)",
            Domains = ["gmx.com", "gmx.us", "gmx.co.uk", "gmx.fr"],
            Imap = Tls("imap.gmx.com"), Smtp = StartTls("mail.gmx.com"),
            Hint = "Aktivieren Sie einmalig in den GMX-Einstellungen den Abruf per IMAP.",
        },
        new()
        {
            Id = "webde", Name = "WEB.DE",
            Domains = ["web.de"],
            Imap = Tls("imap.web.de"), Smtp = StartTls("smtp.web.de"),
            Hint = "Aktivieren Sie einmalig in den WEB.DE-Einstellungen unter „POP3 & IMAP“ den Abruf per IMAP.",
            HelpUrl = "https://hilfe.web.de/pop-imap/einschalten.html",
        },
        new()
        {
            Id = "tonline", Name = "Telekom (t-online.de)",
            Domains = ["t-online.de", "magenta.de"],
            Imap = Tls("secureimap.t-online.de"), Smtp = Tls("securesmtp.t-online.de", 465),
            PasswordLabel = "E-Mail-Passwort",
            Hint = "Nutzen Sie das separate E-Mail-Passwort aus dem Telekom E-Mail Center, nicht Ihr Telekom-Login-Passwort.",
        },
        new()
        {
            Id = "posteo", Name = "Posteo",
            Domains = ["posteo.de", "posteo.net", "posteo.at", "posteo.ch", "posteo.eu", "posteo.org"],
            Imap = Tls("posteo.de"), Smtp = Tls("posteo.de", 465),
        },
        new()
        {
            Id = "mailboxorg", Name = "mailbox.org",
            Domains = ["mailbox.org"],
            Imap = Tls("imap.mailbox.org"), Smtp = Tls("smtp.mailbox.org", 465),
        },
        new()
        {
            Id = "aol", Name = "AOL Mail",
            Domains = ["aol.com", "aol.de", "aim.com"],
            Imap = Tls("imap.aol.com"), Smtp = Tls("smtp.aol.com", 465),
            PasswordLabel = "App-Passwort",
            Hint = "Erstellen Sie in den AOL-Kontoeinstellungen ein App-Passwort.",
        },
        new()
        {
            Id = "freenet", Name = "freenet Mail",
            Domains = ["freenet.de"],
            Imap = Tls("mx.freenet.de"), Smtp = StartTls("mx.freenet.de"),
        },
    ];

    public static ProviderPreset? ForEmail(string email)
    {
        var domain = DomainOf(email);
        return domain is null ? null : All.FirstOrDefault(p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));
    }

    public static ProviderPreset? ById(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static string? DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1 ? email[(at + 1)..].Trim().ToLowerInvariant() : null;
    }
}
