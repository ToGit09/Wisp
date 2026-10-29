using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Wisp.Core.Accounts;
using SecurityMode = Wisp.Core.Accounts.Security;

namespace Wisp.Core.Mail;

public enum FailureKind
{
    /// <summary>Network trouble, timeouts, server busy: try again later.</summary>
    Transient,
    /// <summary>Wrong password / revoked token: the user has to act.</summary>
    Authentication,
    /// <summary>TLS certificate rejected: the user has to act (possible attack).</summary>
    Certificate,
    /// <summary>The server refused this specific request (NO/BAD/5xx).</summary>
    Rejected,
}

/// <summary>Opens authenticated, always-encrypted IMAP and SMTP connections.</summary>
public sealed class MailConnector
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    const int CommandTimeoutMs = 60_000;

    /// <summary>Supplies credentials and persists refreshed OAuth tokens.</summary>
    public delegate Task<Credentials> CredentialSource(CancellationToken ct);

    public static async Task<ImapClient> ConnectImapAsync(AccountConfig account, CredentialSource credentials, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = CommandTimeoutMs };
        try
        {
            using (var connect = Linked(ct, ConnectTimeout))
                await client.ConnectAsync(account.Imap.Host, account.Imap.Port, Options(account.Imap.Security), connect.Token);

            await AuthenticateAsync(client, account, await credentials(ct), ct);

            // Less traffic on slow links.
            if (client.Capabilities.HasFlag(ImapCapabilities.Compress))
                await client.CompressAsync(ct);
            if (client.Capabilities.HasFlag(ImapCapabilities.UTF8Accept))
                await client.EnableUTF8Async(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<SmtpClient> ConnectSmtpAsync(AccountConfig account, CredentialSource credentials, CancellationToken ct)
    {
        var client = new SmtpClient { Timeout = CommandTimeoutMs };
        try
        {
            using (var connect = Linked(ct, ConnectTimeout))
                await client.ConnectAsync(account.Smtp.Host, account.Smtp.Port, Options(account.Smtp.Security), connect.Token);
            await AuthenticateAsync(client, account, await credentials(ct), ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Checks login on both servers before an account is saved.</summary>
    public static async Task TestAsync(AccountConfig account, Credentials credentials, CancellationToken ct)
    {
        CredentialSource source = _ => Task.FromResult(credentials);
        using (var imap = await ConnectImapAsync(account, source, ct))
            await imap.DisconnectAsync(true, ct);
        using (var smtp = await ConnectSmtpAsync(account, source, ct))
            await smtp.DisconnectAsync(true, ct);
    }

    /// <summary>Returns credentials whose OAuth access token is valid, refreshing it if needed.</summary>
    public static async Task<(Credentials Credentials, bool Refreshed)> EnsureFreshAsync(AccountConfig account, Credentials current, CancellationToken ct)
    {
        if (OAuthConfig.For(account.Auth) is not { } provider) return (current, false);
        if (current.AccessToken is not null && current.AccessTokenExpires > DateTimeOffset.UtcNow) return (current, false);
        return (await OAuthFlow.RefreshAsync(provider, current, ct), true);
    }

    static async Task AuthenticateAsync(IMailService client, AccountConfig account, Credentials credentials, CancellationToken ct)
    {
        if (account.Auth == AuthKind.Password)
            await client.AuthenticateAsync(account.UserName, credentials.Password ?? "", ct);
        else
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.UserName, credentials.AccessToken ?? ""), ct);
    }

    // There is deliberately no option for unencrypted connections.
    static SecureSocketOptions Options(SecurityMode security) =>
        security == SecurityMode.Tls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    public static FailureKind Classify(Exception ex) => ex switch
    {
        AuthenticationException => FailureKind.Authentication,
        UnauthorizedAccessException => FailureKind.Authentication,
        SslHandshakeException => FailureKind.Certificate,
        ImapCommandException => FailureKind.Rejected,
        SmtpCommandException smtp when (int)smtp.StatusCode >= 500 => FailureKind.Rejected,
        FolderNotFoundException => FailureKind.Rejected,
        MessageNotFoundException => FailureKind.Rejected,
        _ => FailureKind.Transient,
    };

    /// <summary>A short German explanation for the user. Never contains secrets.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AuthenticationException or UnauthorizedAccessException =>
            "Anmeldung abgelehnt. Bitte Passwort bzw. App-Passwort prüfen.",
        SslHandshakeException =>
            "Die sichere Verbindung wurde abgelehnt: Das Zertifikat des Servers ist ungültig.",
        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            "Server nicht gefunden. Besteht eine Internetverbindung?",
        SocketException or IOException or OperationCanceledException or TimeoutException
            or ProtocolException or ServiceNotConnectedException =>
            "Keine Verbindung zum Server.",
        SmtpCommandException smtp when smtp.ErrorCode == SmtpErrorCode.RecipientNotAccepted =>
            $"Empfänger wurde abgelehnt: {smtp.Mailbox?.Address}",
        SmtpCommandException smtp => $"Der Server hat die Mail abgelehnt: {smtp.Message}",
        ImapCommandException imap => $"Der Server hat die Aktion abgelehnt: {imap.ResponseText}",
        InvalidOperationException => ex.Message,
        _ => "Unerwarteter Fehler: " + ex.Message,
    };
}
