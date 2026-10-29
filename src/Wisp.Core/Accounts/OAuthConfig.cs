namespace Wisp.Core.Accounts;

public sealed record OAuthProvider(
    string Name,
    string AuthorizeUrl,
    string TokenUrl,
    string Scopes,
    string ClientId,
    string? ClientSecret,
    string RedirectHost)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// OAuth client registrations. Each published build needs its own IDs (see docs/ANBIETER.md).
/// They can also be supplied at runtime via WISP_GOOGLE_CLIENT_ID / WISP_GOOGLE_CLIENT_SECRET /
/// WISP_MICROSOFT_CLIENT_ID. Desktop "client secrets" are not confidential by design (RFC 8252); PKCE protects the flow.
/// </summary>
public static class OAuthConfig
{
    public static OAuthProvider Google { get; } = new(
        "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://mail.google.com/",
        Environment.GetEnvironmentVariable("WISP_GOOGLE_CLIENT_ID") ?? "",
        Environment.GetEnvironmentVariable("WISP_GOOGLE_CLIENT_SECRET"),
        "127.0.0.1");

    public static OAuthProvider Microsoft { get; } = new(
        "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
        Environment.GetEnvironmentVariable("WISP_MICROSOFT_CLIENT_ID") ?? "",
        null,
        "localhost");

    public static OAuthProvider? For(AuthKind kind) => kind switch
    {
        AuthKind.OAuthGoogle => Google,
        AuthKind.OAuthMicrosoft => Microsoft,
        _ => null,
    };
}
