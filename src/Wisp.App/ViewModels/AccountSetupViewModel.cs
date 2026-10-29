using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core;
using Wisp.Core.Accounts;
using Wisp.Core.Mail;
using SecurityMode = Wisp.Core.Accounts.Security;

namespace Wisp.App.ViewModels;

/// <summary>Two steps: address → login. Server details are filled in automatically and stay hidden unless needed.</summary>
public sealed partial class AccountSetupViewModel : ObservableObject
{
    readonly MailService _mail;
    CancellationTokenSource? _cts;
    ProviderPreset? _preset;

    public AccountSetupViewModel(MailService mail) => _mail = mail;

    public IReadOnlyList<Choice<SecurityMode>> SecurityChoices { get; } =
        [new("SSL/TLS", SecurityMode.Tls), new("STARTTLS", SecurityMode.StartTls)];

    [ObservableProperty] public partial int Step { get; set; } = 1;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Email { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string PasswordLabel { get; set; } = "Passwort";
    [ObservableProperty] public partial string ProviderName { get; set; } = "";
    [ObservableProperty] public partial string? Hint { get; set; }
    [ObservableProperty] public partial string? HelpUrl { get; set; }
    [ObservableProperty] public partial bool UsesOAuth { get; set; }
    [ObservableProperty] public partial bool OAuthAlternative { get; set; }
    [ObservableProperty] public partial bool ShowServers { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Progress { get; set; }

    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string ImapHost { get; set; } = "";
    [ObservableProperty] public partial decimal? ImapPort { get; set; } = 993;
    [ObservableProperty] public partial Choice<SecurityMode>? ImapSecurity { get; set; }
    [ObservableProperty] public partial string SmtpHost { get; set; } = "";
    [ObservableProperty] public partial decimal? SmtpPort { get; set; } = 465;
    [ObservableProperty] public partial Choice<SecurityMode>? SmtpSecurity { get; set; }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool ShowPassword => !UsesOAuth;
    public string OAuthButtonText => _preset?.Auth == AuthKind.OAuthMicrosoft ? "Bei Microsoft anmelden" : "Mit Google anmelden";

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
    }

    partial void OnUsesOAuthChanged(bool value) => OnPropertyChanged(nameof(ShowPassword));

    public event Action? Completed;

    [RelayCommand]
    async Task Next()
    {
        Error = null;
        Email = Email.Trim();
        if (ProviderPresets.DomainOf(Email) is null || !Email.Contains('@'))
        {
            Error = "Bitte eine vollständige E-Mail-Adresse eingeben.";
            return;
        }

        _preset = ProviderPresets.ForEmail(Email);
        if (_preset is not null)
        {
            Fill(_preset.Imap, _preset.Smtp, Email);
            ProviderName = _preset.Name;
            Hint = _preset.Hint;
            HelpUrl = _preset.HelpUrl;
            PasswordLabel = _preset.PasswordLabel;
            UsesOAuth = _preset.Auth != AuthKind.Password;
            OAuthAlternative = _preset.AlternativeAuth is { } alt && OAuthConfig.For(alt)?.IsConfigured == true;
            ShowServers = false;
        }
        else
        {
            IsBusy = true;
            Progress = "Suche Servereinstellungen …";
            _cts = new CancellationTokenSource();
            var found = await AutoConfig.DiscoverAsync(Email, _cts.Token);
            IsBusy = false;
            Progress = null;
            if (found is not null)
            {
                Fill(found.Imap, found.Smtp, found.UserName);
                ProviderName = found.ProviderName ?? ProviderPresets.DomainOf(Email)!;
                ShowServers = false;
            }
            else
            {
                var domain = ProviderPresets.DomainOf(Email)!;
                Fill(new ServerSettings("imap." + domain, 993, SecurityMode.Tls), new ServerSettings("smtp." + domain, 465, SecurityMode.Tls), Email);
                ProviderName = domain;
                Hint = "Wisp konnte die Servereinstellungen nicht automatisch finden. Bitte prüfen Sie die Angaben unten (Ihr Anbieter nennt sie in seiner Hilfe).";
                ShowServers = true;
            }
            PasswordLabel = "Passwort";
            UsesOAuth = false;
        }
        Step = 2;
    }

    void Fill(ServerSettings imap, ServerSettings smtp, string userName)
    {
        ImapHost = imap.Host;
        ImapPort = imap.Port;
        ImapSecurity = SecurityChoices.First(c => c.Value == imap.Security);
        SmtpHost = smtp.Host;
        SmtpPort = smtp.Port;
        SmtpSecurity = SecurityChoices.First(c => c.Value == smtp.Security);
        UserName = userName;
    }

    [RelayCommand]
    void Back()
    {
        _cts?.Cancel();
        Error = null;
        Step = 1;
    }

    [RelayCommand]
    void ToggleServers() => ShowServers = !ShowServers;

    [RelayCommand]
    void OpenHelp()
    {
        if (HelpUrl is { } url) OAuthFlow.OpenBrowser(url);
    }

    [RelayCommand]
    Task Connect() => ConnectAsync(UsesOAuth && _preset is not null ? _preset.Auth : AuthKind.Password);

    /// <summary>Browser login: the provider's main method, or its alternative (e.g. Gmail).</summary>
    [RelayCommand]
    Task ConnectWithOAuth() => ConnectAsync(_preset switch
    {
        null => AuthKind.Password,
        { Auth: not AuthKind.Password } p => p.Auth,
        { AlternativeAuth: { } alternative } => alternative,
        _ => AuthKind.Password,
    });

    async Task ConnectAsync(AuthKind auth)
    {
        Error = null;
        if (auth == AuthKind.Password && Password.Length == 0)
        {
            Error = $"Bitte {PasswordLabel} eingeben.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost))
        {
            Error = "Bitte die Server angeben.";
            ShowServers = true;
            return;
        }

        var config = new AccountConfig
        {
            DisplayName = Name.Trim(),
            Email = Email,
            UserName = string.IsNullOrWhiteSpace(UserName) ? Email : UserName.Trim(),
            Imap = new ServerSettings(ImapHost.Trim(), (int)(ImapPort ?? 993), ImapSecurity?.Value ?? SecurityMode.Tls),
            Smtp = new ServerSettings(SmtpHost.Trim(), (int)(SmtpPort ?? 465), SmtpSecurity?.Value ?? SecurityMode.Tls),
            Auth = auth,
            ProviderId = _preset?.Id,
            ServerSavesSent = _preset?.ServerSavesSent ?? false,
        };

        IsBusy = true;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            Credentials credentials;
            if (auth == AuthKind.Password)
            {
                credentials = Credentials.FromPassword(Password);
            }
            else
            {
                Progress = "Bitte im Browser anmelden …";
                credentials = await OAuthFlow.AuthorizeAsync(OAuthConfig.For(auth)!, Email, _cts.Token);
            }
            Progress = "Verbinde und prüfe die Anmeldung …";
            await _mail.AddAccountAsync(config, credentials, _cts.Token);
            Password = "";
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            Error = MailConnector.Describe(ex);
            if (MailConnector.Classify(ex) == FailureKind.Transient && ex is not InvalidOperationException) ShowServers = true;
        }
        finally
        {
            IsBusy = false;
            Progress = null;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
