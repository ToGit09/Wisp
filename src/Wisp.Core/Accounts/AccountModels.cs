namespace Wisp.Core.Accounts;

/// <summary>Transport security. There is intentionally no "none": Wisp never talks to a mail server unencrypted.</summary>
public enum Security
{
    /// <summary>TLS from the first byte (IMAPS 993, SMTPS 465).</summary>
    Tls,
    /// <summary>Plain connect, then mandatory STARTTLS (587/143). Fails if the server does not offer it.</summary>
    StartTls,
}

public enum AuthKind
{
    Password,
    OAuthGoogle,
    OAuthMicrosoft,
}

public sealed record ServerSettings(string Host, int Port, Security Security);

/// <summary>Everything about an account except the secret. Stored encrypted.</summary>
public sealed record AccountConfig
{
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public required string UserName { get; init; }
    public required ServerSettings Imap { get; init; }
    public required ServerSettings Smtp { get; init; }
    public AuthKind Auth { get; init; } = AuthKind.Password;
    public string? ProviderId { get; init; }
    /// <summary>The provider files a copy into "Sent" by itself after SMTP submission (Gmail, Outlook).</summary>
    public bool ServerSavesSent { get; init; }
}

/// <summary>The secret part of an account. Stored encrypted, never logged.</summary>
public sealed record Credentials
{
    public string? Password { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? AccessTokenExpires { get; init; }

    public static Credentials FromPassword(string password) => new() { Password = password };

    public override string ToString() => "Credentials(***)";
}

public sealed record ProviderPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string[] Domains { get; init; }
    public required ServerSettings Imap { get; init; }
    public required ServerSettings Smtp { get; init; }
    public AuthKind Auth { get; init; } = AuthKind.Password;
    /// <summary>Alternative auth the user may pick (e.g. Gmail: app password while OAuth is not verified).</summary>
    public AuthKind? AlternativeAuth { get; init; }
    public bool ServerSavesSent { get; init; }
    /// <summary>Short, non-technical German hint shown in the setup dialog.</summary>
    public string? Hint { get; init; }
    public string? HelpUrl { get; init; }
    public string PasswordLabel { get; init; } = "Passwort";
}
