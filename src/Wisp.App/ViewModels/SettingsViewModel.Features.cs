using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core;
using Wisp.Core.Settings;
using Wisp.Core.Storage;

namespace Wisp.App.ViewModels;

public sealed record SettingsSection(string Title, string Icon)
{
    public Avalonia.Media.Geometry? IconData => SidebarItem.Find(Icon);
}

/// <summary>One folder in "Synchronisation → Ordner": synced automatically or only when opened.</summary>
public sealed partial class FolderSyncItem(long folderId, string account, string name, bool enabled, Action<long, bool> changed) : ObservableObject
{
    public string Account { get; } = account;
    public string Name { get; } = name;

    [ObservableProperty] public partial bool Enabled { get; set; } = enabled;

    partial void OnEnabledChanged(bool value) => changed(folderId, value);
}

/// <summary>The feature switches. Each one writes straight into the settings and saves.</summary>
public sealed partial class SettingsViewModel
{
    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new("Darstellung", "IconImage"),
        new("Lesen", "IconMail"),
        new("Schreiben", "IconCompose"),
        new("Synchronisation", "IconRefresh"),
        new("Datenschutz & Sicherheit", "IconLock"),
        new("Konten", "IconInbox"),
        new("Über Wisp", "IconSettings"),
    ];

    [ObservableProperty] public partial int SectionIndex { get; set; }

    bool Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        OnPropertyChanged(name);
        Save();
        return true;
    }

    AppearanceSettings A => _s.Appearance;
    ReadingSettings R => _s.Reading;
    ComposeSettings C => _s.Compose;

    // ───────────────────────── Darstellung ─────────────────────────

    public bool ShowAvatars { get => A.ShowAvatars; set => Set(A.ShowAvatars, value, v => A.ShowAvatars = v); }
    public bool AutoCollapseSidebar { get => A.AutoCollapseSidebar; set => Set(A.AutoCollapseSidebar, value, v => A.AutoCollapseSidebar = v); }
    public bool UnreadInTitle { get => A.UnreadInTitle; set => Set(A.UnreadInTitle, value, v => A.UnreadInTitle = v); }
    public bool QuickActions { get => A.QuickActions; set => Set(A.QuickActions, value, v => A.QuickActions = v); }
    public bool ContextMenu { get => A.ContextMenu; set => Set(A.ContextMenu, value, v => A.ContextMenu = v); }

    /// <summary>Zoom of HTML mails in percent (slider 50–200).</summary>
    public double HtmlZoomPercent
    {
        get => Math.Round(A.HtmlZoom * 100);
        set
        {
            if (Set(A.HtmlZoom, Math.Round(value / 10) / 10, v => A.HtmlZoom = v)) OnPropertyChanged(nameof(HtmlZoomText));
        }
    }

    public string HtmlZoomText => $"{A.HtmlZoom * 100:0} %";

    // ───────────────────────── Lesen ─────────────────────────

    public bool MarkReadOnOpen { get => R.MarkReadOnOpen; set => Set(R.MarkReadOnOpen, value, v => R.MarkReadOnOpen = v); }
    public bool ShowLinkList { get => R.ShowLinkList; set => Set(R.ShowLinkList, value, v => R.ShowLinkList = v); }
    public bool WarnSuspiciousLinks { get => R.WarnSuspiciousLinks; set => Set(R.WarnSuspiciousLinks, value, v => R.WarnSuspiciousLinks = v); }

    public IReadOnlyList<Choice<LinkConfirmation>> LinkConfirmations { get; } =
        [new("Nur bei verdächtigen Links", LinkConfirmation.SuspiciousOnly), new("Bei jedem Link", LinkConfirmation.Always)];

    public Choice<LinkConfirmation> ConfirmLinks
    {
        get => LinkConfirmations.First(c => c.Value == R.ConfirmLinks);
        set { if (value is not null) Set(R.ConfirmLinks, value.Value, v => R.ConfirmLinks = v); }
    }

    // ───────────────────────── Schreiben ─────────────────────────

    public IReadOnlyList<Choice<bool>> Editors { get; } =
        [new("Light – einfacher Text", false), new("Advanced – HTML mit Formatierung", true)];

    public Choice<bool> DefaultEditor
    {
        get => Editors.First(e => e.Value == C.Advanced);
        set { if (value is not null) Set(C.Advanced, value.Value, v => C.Advanced = v); }
    }

    public bool SignatureEnabled { get => C.SignatureEnabled; set => Set(C.SignatureEnabled, value, v => C.SignatureEnabled = v); }
    public string Signature { get => C.Signature; set => Set(C.Signature, value ?? "", v => C.Signature = v); }
    public bool QuoteOnReply { get => C.QuoteOnReply; set => Set(C.QuoteOnReply, value, v => C.QuoteOnReply = v); }
    public bool CtrlEnterSends { get => C.CtrlEnterSends; set => Set(C.CtrlEnterSends, value, v => C.CtrlEnterSends = v); }
    public bool ConfirmDiscard { get => C.ConfirmDiscard; set => Set(C.ConfirmDiscard, value, v => C.ConfirmDiscard = v); }

    public IReadOnlyList<Choice<int>> SendDelays { get; } =
        [new("Sofort senden", 0), new("5 Sekunden", 5), new("10 Sekunden", 10), new("20 Sekunden", 20), new("30 Sekunden", 30)];

    public Choice<int> SendDelay
    {
        get => SendDelays.FirstOrDefault(d => d.Value == C.SendDelaySeconds) ?? SendDelays[1];
        set { if (value is not null) Set(C.SendDelaySeconds, value.Value, v => C.SendDelaySeconds = v); }
    }

    // ───────────────────────── Synchronisation ─────────────────────────

    public bool UsePush { get => _s.Sync.UsePush; set => Set(_s.Sync.UsePush, value, v => _s.Sync.UsePush = v); }

    public ObservableCollection<FolderSyncItem> FolderSync { get; } = [];

    void LoadFolderSync()
    {
        FolderSync.Clear();
        var accounts = _mail.GetAccounts();
        foreach (var account in accounts)
        {
            foreach (var folder in _mail.GetFolders(account.Id).Where(f => f.Role != FolderRole.Inbox))
            {
                FolderSync.Add(new FolderSyncItem(folder.Id, accounts.Count > 1 ? account.Config.Email : "",
                    SidebarItem.DisplayName(folder.Role, folder.Name), folder.SyncEnabled,
                    (id, enabled) => _mail.SetFolderSync(id, enabled)));
            }
        }
    }

    // ───────────────────────── Datenschutz ─────────────────────────

    public bool WriteCrashLog
    {
        get => _s.Privacy.WriteCrashLog;
        set
        {
            if (Set(_s.Privacy.WriteCrashLog, value, v => _s.Privacy.WriteCrashLog = v)) CrashLog.Enabled = value;
        }
    }

    // ───────────────────────── Über ─────────────────────────

    public string Version => $"Version {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string DataFolder => AppPaths.DataDir;

    [RelayCommand]
    void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
        catch (Exception) { /* no file manager available */ }
    }
}
