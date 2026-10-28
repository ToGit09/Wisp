using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.App.Theme;
using Wisp.Core;
using Wisp.Core.Accounts;
using Wisp.Core.Security;
using Wisp.Core.Settings;

namespace Wisp.App.ViewModels;

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed record AccentChoice(string Name, string Hex);

public sealed record AccountEntry(long Id, string Email, string Provider, bool UsesOAuth);

/// <summary>Every option applies immediately so the user sees the effect while choosing.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly MailService _mail;
    readonly AppSettings _s;
    readonly Vault _vault;
    readonly IDialogs _dialogs;

    public SettingsViewModel(MailService mail, Vault vault, IDialogs dialogs)
    {
        _mail = mail;
        _s = mail.Settings;
        _vault = vault;
        _dialogs = dialogs;
        Theme = Themes.First(t => t.Value == _s.Appearance.Theme);
        Font = Fonts.FirstOrDefault(f => f.Value == _s.Appearance.Font) ?? Fonts[0];
        FontSize = _s.Appearance.FontSize;
        Density = Densities.First(d => d.Value == _s.Appearance.Density);
        ShowPreview = _s.Appearance.ShowPreview;
        AccentHex = _s.Appearance.Accent;
        ShowHtml = _s.Privacy.ShowHtml;
        LoadRemoteImages = _s.Privacy.LoadRemoteImages;
        OfflineDays = _s.Sync.OfflineDays;
        PrefetchMaxMb = Math.Max(1, _s.Sync.PrefetchMaxKb / 1024);
        HeadersPerFolder = _s.Sync.HeadersPerFolder;
        PollMinutes = _s.Sync.PollMinutes;
        ReloadAccounts();
        LoadFolderSync();
    }

    public IReadOnlyList<Choice<ThemeMode>> Themes { get; } =
        [new("Wie das System", ThemeMode.System), new("Hell", ThemeMode.Light), new("Dunkel", ThemeMode.Dark)];
    public IReadOnlyList<Choice<string>> Fonts { get; } = ThemeManager.Fonts.Select(f => new Choice<string>(f.Name, f.Value)).ToList();
    public IReadOnlyList<Choice<Density>> Densities { get; } =
        [new("Kompakt", Core.Settings.Density.Compact), new("Normal", Core.Settings.Density.Normal), new("Luftig", Core.Settings.Density.Relaxed)];
    public IReadOnlyList<AccentChoice> Accents { get; } = ThemeManager.AccentPresets.Select(a => new AccentChoice(a.Name, a.Hex)).ToList();
    public ObservableCollection<AccountEntry> AccountList { get; } = [];

    [ObservableProperty] public partial Choice<ThemeMode> Theme { get; set; }
    [ObservableProperty] public partial Choice<string> Font { get; set; }
    [ObservableProperty] public partial double FontSize { get; set; }
    [ObservableProperty] public partial Choice<Density> Density { get; set; }
    [ObservableProperty] public partial bool ShowPreview { get; set; }
    [ObservableProperty] public partial string AccentHex { get; set; }
    [ObservableProperty] public partial bool ShowHtml { get; set; }
    [ObservableProperty] public partial bool LoadRemoteImages { get; set; }
    [ObservableProperty] public partial decimal? OfflineDays { get; set; }
    [ObservableProperty] public partial decimal? PrefetchMaxMb { get; set; }
    [ObservableProperty] public partial decimal? HeadersPerFolder { get; set; }
    [ObservableProperty] public partial decimal? PollMinutes { get; set; }
    [ObservableProperty] public partial string? AccountMessage { get; set; }
    [ObservableProperty] public partial string NewMasterPassword { get; set; } = "";
    [ObservableProperty] public partial string NewMasterPasswordRepeat { get; set; } = "";
    [ObservableProperty] public partial string CurrentMasterPassword { get; set; } = "";
    [ObservableProperty] public partial string? SecurityMessage { get; set; }

    public string FontSizeText => $"{FontSize:0} pt";
    public bool HasMasterPassword => _vault.Protection == VaultProtection.MasterPassword;
    public bool CanRemoveMasterPassword => HasMasterPassword && Vault.SystemProtectionAvailable;
    public string ProtectionText => HasMasterPassword
        ? "Ihre Daten sind mit einem Master-Passwort geschützt. Wisp fragt beim Start danach."
        : OperatingSystem.IsWindows()
            ? "Ihre Daten sind mit Ihrem Windows-Benutzerkonto verschlüsselt."
            : "Ihre Daten sind mit dem Schlüsselbund Ihres Benutzerkontos verschlüsselt.";

    partial void OnThemeChanged(Choice<ThemeMode> value) { _s.Appearance.Theme = value.Value; Apply(); }
    partial void OnFontChanged(Choice<string> value) { _s.Appearance.Font = value.Value; Apply(); }
    partial void OnFontSizeChanged(double value)
    {
        _s.Appearance.FontSize = Math.Round(value);
        OnPropertyChanged(nameof(FontSizeText));
        Apply();
    }
    partial void OnDensityChanged(Choice<Density> value) { _s.Appearance.Density = value.Value; Apply(); }
    partial void OnShowPreviewChanged(bool value) { _s.Appearance.ShowPreview = value; Save(); }
    partial void OnAccentHexChanged(string value)
    {
        if (ThemeManager.TryParse(value) is null) return;
        _s.Appearance.Accent = value;
        Apply();
    }
    partial void OnShowHtmlChanged(bool value) { _s.Privacy.ShowHtml = value; Save(); }
    partial void OnLoadRemoteImagesChanged(bool value) { _s.Privacy.LoadRemoteImages = value; Save(); }
    partial void OnOfflineDaysChanged(decimal? value) { _s.Sync.OfflineDays = (int)(value ?? 14); Save(); }
    partial void OnPrefetchMaxMbChanged(decimal? value) { _s.Sync.PrefetchMaxKb = (int)(value ?? 2) * 1024; Save(); }
    partial void OnHeadersPerFolderChanged(decimal? value) { _s.Sync.HeadersPerFolder = (int)(value ?? 5000); Save(); }
    partial void OnPollMinutesChanged(decimal? value) { _s.Sync.PollMinutes = (int)(value ?? 5); Save(); }

    [RelayCommand]
    void PickAccent(string? hex)
    {
        if (hex is not null) AccentHex = hex;
    }

    [RelayCommand]
    void ResetAppearance()
    {
        var defaults = new AppearanceSettings();
        Theme = Themes.First(t => t.Value == defaults.Theme);
        Font = Fonts.First(f => f.Value == defaults.Font);
        FontSize = defaults.FontSize;
        Density = Densities.First(d => d.Value == defaults.Density);
        ShowPreview = defaults.ShowPreview;
        AccentHex = defaults.Accent;
        ShowAvatars = defaults.ShowAvatars;
        AutoCollapseSidebar = defaults.AutoCollapseSidebar;
        UnreadInTitle = defaults.UnreadInTitle;
        QuickActions = defaults.QuickActions;
        ContextMenu = defaults.ContextMenu;
        HtmlZoomPercent = defaults.HtmlZoom * 100;
    }

    void Apply()
    {
        ThemeManager.Apply(_s.Appearance);
        Save();
    }

    void Save()
    {
        try { _mail.SaveSettings(); }
        catch (IOException) { /* retried on the next change */ }
    }

    // ───────────────────────── Accounts ─────────────────────────

    void ReloadAccounts()
    {
        AccountList.Clear();
        foreach (var a in _mail.GetAccounts())
            AccountList.Add(new AccountEntry(a.Id, a.Config.Email, ProviderPresets.ById(a.Config.ProviderId)?.Name ?? a.Config.Imap.Host,
                a.Config.Auth != AuthKind.Password));
    }

    [RelayCommand]
    async Task AddAccount()
    {
        await _dialogs.OpenAccountSetupAsync();
        ReloadAccounts();
        LoadFolderSync();
    }

    [RelayCommand]
    async Task RemoveAccount(AccountEntry? entry)
    {
        if (entry is null) return;
        var ok = await _dialogs.ConfirmAsync("Konto entfernen?",
            $"{entry.Email} wird von diesem Gerät entfernt, inklusive aller lokal gespeicherten Mails. " +
            "Auf dem Server bleibt alles erhalten – Sie können das Konto jederzeit wieder hinzufügen.",
            "Von diesem Gerät entfernen", danger: true);
        if (!ok) return;
        await _mail.RemoveAccountAsync(entry.Id);
        ReloadAccounts();
    }

    [RelayCommand]
    async Task Reauthorize(AccountEntry? entry)
    {
        if (entry is null) return;
        try
        {
            AccountMessage = "Bitte im Browser anmelden …";
            await _mail.ReauthorizeAsync(entry.Id, CancellationToken.None);
            AccountMessage = "Erneut angemeldet.";
        }
        catch (Exception ex)
        {
            AccountMessage = Core.Mail.MailConnector.Describe(ex);
        }
    }

    [ObservableProperty] public partial string PasswordForAccount { get; set; } = "";
    [ObservableProperty] public partial AccountEntry? PasswordAccount { get; set; }

    [RelayCommand]
    void BeginPasswordChange(AccountEntry? entry)
    {
        PasswordAccount = entry;
        PasswordForAccount = "";
        AccountMessage = null;
    }

    [RelayCommand]
    async Task SavePassword()
    {
        if (PasswordAccount is not { } entry || PasswordForAccount.Length == 0) return;
        AccountMessage = "Prüfe Anmeldung …";
        try
        {
            await _mail.UpdatePasswordAsync(entry.Id, PasswordForAccount, CancellationToken.None);
            AccountMessage = "Passwort gespeichert.";
            PasswordAccount = null;
        }
        catch (Exception ex)
        {
            AccountMessage = Core.Mail.MailConnector.Describe(ex);
        }
        finally
        {
            PasswordForAccount = "";
        }
    }

    // ───────────────────────── Master password ─────────────────────────

    [RelayCommand]
    void SetMasterPassword()
    {
        SecurityMessage = null;
        if (NewMasterPassword.Length < 8) { SecurityMessage = "Bitte mindestens 8 Zeichen verwenden."; return; }
        if (NewMasterPassword != NewMasterPasswordRepeat) { SecurityMessage = "Die Passwörter stimmen nicht überein."; return; }
        try
        {
            var key = HasMasterPassword ? _vault.UnlockWithPassword(CurrentMasterPassword) : _vault.UnlockWithSystem();
            _vault.ChangeProtection(key, VaultProtection.MasterPassword, NewMasterPassword);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            SecurityMessage = "Master-Passwort gespeichert. Ohne dieses Passwort lassen sich Ihre Daten nicht mehr öffnen – bitte gut merken.";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            SecurityMessage = "Das aktuelle Master-Passwort ist falsch.";
        }
        NewMasterPassword = NewMasterPasswordRepeat = CurrentMasterPassword = "";
        RaiseSecurity();
    }

    [RelayCommand]
    void RemoveMasterPassword()
    {
        try
        {
            var key = _vault.UnlockWithPassword(CurrentMasterPassword);
            _vault.ChangeProtection(key, VaultProtection.System);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            SecurityMessage = "Master-Passwort entfernt. Ihre Daten sind jetzt mit Ihrem Benutzerkonto geschützt.";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            SecurityMessage = "Das aktuelle Master-Passwort ist falsch.";
        }
        CurrentMasterPassword = "";
        RaiseSecurity();
    }

    void RaiseSecurity()
    {
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(CanRemoveMasterPassword));
        OnPropertyChanged(nameof(ProtectionText));
    }
}
