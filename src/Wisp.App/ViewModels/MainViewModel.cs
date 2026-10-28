using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core;
using Wisp.Core.Mail;
using Wisp.Core.Rendering;
using Wisp.Core.Storage;
using Wisp.Core.Sync;

namespace Wisp.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    readonly MailService _mail;
    readonly IDialogs _dialogs;
    readonly DispatcherTimer _refreshTimer;
    readonly DispatcherTimer _searchTimer;
    readonly DispatcherTimer _noticeTimer;
    bool _sidebarDirty, _messagesDirty, _statusDirty, _outboxDirty;
    CancellationTokenSource? _openCts;
    IReadOnlyList<long> _selection = [];

    public MainViewModel(MailService mail, IDialogs dialogs)
    {
        _mail = mail;
        _dialogs = dialogs;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FlushChanges());
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            ReloadMessages();
        });
        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) =>
        {
            _noticeTimer!.Stop();
            Notice = null;
        });
        _mail.Changed += OnChanged;
        ReloadSidebar();
        UpdateStatus();
        SelectedSidebarItem = Sidebar.FirstOrDefault(s => s.Kind == SidebarKind.Folder && s.Role == FolderRole.Inbox)
                              ?? Sidebar.FirstOrDefault(s => s.IsSelectable);
    }

    public ObservableCollection<SidebarItem> Sidebar { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<OutboxEntry> Outbox { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public ObservableCollection<LinkItem> Links { get; } = [];
    public ObservableCollection<SidebarItem> MoveTargets { get; } = [];

    public bool HasAccounts => _mail.GetAccounts().Count > 0;
    public bool ShowPreview => _mail.Settings.Appearance.ShowPreview;

    [ObservableProperty] public partial SidebarItem? SelectedSidebarItem { get; set; }
    [ObservableProperty] public partial string FolderTitle { get; set; } = "";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string? FolderWarning { get; set; }
    [ObservableProperty] public partial bool IsOutboxSelected { get; set; }
    [ObservableProperty] public partial bool IsListEmpty { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string StatusKind { get; set; } = "offline";
    [ObservableProperty] public partial string? Notice { get; set; }

    // Reader
    [ObservableProperty] public partial RenderedMail? CurrentMail { get; set; }
    [ObservableProperty] public partial long? CurrentMessageId { get; set; }
    [ObservableProperty] public partial string? ReaderProblem { get; set; }
    [ObservableProperty] public partial bool IsLoadingMail { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool CurrentIsUnread { get; set; }
    [ObservableProperty] public partial bool CurrentIsFlagged { get; set; }
    /// <summary>Per-mail override of the global "show HTML" setting.</summary>
    [ObservableProperty] public partial bool ShowHtmlForCurrent { get; set; }
    [ObservableProperty] public partial bool ImagesAllowedForCurrent { get; set; }

    public bool ShowRemoteImageBanner => CurrentMail is { HasRemoteImages: true, RemoteImagesAllowed: false } && ShowHtmlForCurrent;
    public bool ShowHtmlBanner => CurrentMail is { HasHtml: true } && !ShowHtmlForCurrent;
    public bool CanArchive => SelectedSidebarItem is { } s && _mail.ArchiveFolder(s.AccountId) is { } a && a.Id != s.FolderId;

    partial void OnCurrentMailChanged(RenderedMail? value)
    {
        OnPropertyChanged(nameof(ShowRemoteImageBanner));
        OnPropertyChanged(nameof(ShowHtmlBanner));
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSelectedSidebarItemChanged(SidebarItem? value)
    {
        if (value is null) return;
        if (value.IsHeader)
        {
            // Account headers are labels, not folders.
            SelectedSidebarItem = Sidebar.SkipWhile(s => s != value).Skip(1).FirstOrDefault(s => s.IsSelectable);
            return;
        }
        IsOutboxSelected = value.Kind == SidebarKind.Outbox;
        FolderTitle = value.Name;
        SearchText = "";
        if (value.FolderId is long folderId) _mail.FolderOpened(folderId);
        ReloadMessages();
        ReloadMoveTargets();
        ClearReader();
        OnPropertyChanged(nameof(CanArchive));
    }

    // ───────────────────────── Loading ─────────────────────────

    void OnChanged(WorkerChange change)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (change.Kind)
            {
                case ChangeKind.Status: _statusDirty = true; break;
                case ChangeKind.Folders: _sidebarDirty = true; _statusDirty = true; break;
                case ChangeKind.Messages: _messagesDirty = true; _sidebarDirty = true; _statusDirty = true; break;
                case ChangeKind.Outbox: _outboxDirty = true; _sidebarDirty = true; _statusDirty = true; break;
                case ChangeKind.Notice: ShowNotice(change.Text); _sidebarDirty = true; break;
            }
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        });
    }

    void FlushChanges()
    {
        _refreshTimer.Stop();
        if (_sidebarDirty) { _sidebarDirty = false; ReloadSidebar(); }
        if (_messagesDirty || (_outboxDirty && IsOutboxSelected)) { _messagesDirty = _outboxDirty = false; ReloadMessages(); }
        if (_statusDirty) { _statusDirty = false; UpdateStatus(); }
    }

    public void ReloadAll()
    {
        ReloadSidebar();
        ReloadMessages();
        UpdateStatus();
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(ShowPreview));
        if (SelectedSidebarItem is null || !Sidebar.Contains(SelectedSidebarItem))
            SelectedSidebarItem = Sidebar.FirstOrDefault(s => s.IsSelectable);
    }

    void ReloadSidebar()
    {
        var items = new List<SidebarItem>();
        foreach (var account in _mail.GetAccounts())
        {
            var status = _mail.GetStatus(account.Id);
            items.Add(new SidebarItem
            {
                Kind = SidebarKind.Account, AccountId = account.Id,
                Name = account.Config.Email,
                Status = status.State is ConnectionState.AuthFailed or ConnectionState.Error ? "!" : null,
            });
            foreach (var folder in _mail.GetFolders(account.Id))
            {
                items.Add(new SidebarItem
                {
                    Kind = SidebarKind.Folder, AccountId = account.Id, FolderId = folder.Id, Role = folder.Role,
                    Depth = folder.Role == FolderRole.None ? folder.Depth : 0,
                    Name = SidebarItem.DisplayName(folder),
                    Unread = folder.Role is FolderRole.Sent or FolderRole.Trash or FolderRole.Drafts or FolderRole.All ? 0 : folder.Unread,
                    Status = folder.MassDeleteWarning ? "!" : null,
                });
            }
        }
        var outbox = _mail.GetOutbox();
        if (outbox.Count > 0)
            items.Add(new SidebarItem { Kind = SidebarKind.Outbox, Name = "Postausgang", Unread = outbox.Count });

        // Update in place where possible so the selection survives.
        var keep = SelectedSidebarItem;
        for (var i = 0; i < items.Count; i++)
        {
            var fresh = items[i];
            if (i < Sidebar.Count && SameEntry(Sidebar[i], fresh))
            {
                Sidebar[i].Name = fresh.Name;
                Sidebar[i].Unread = fresh.Unread;
                Sidebar[i].Status = fresh.Status;
            }
            else if (i < Sidebar.Count)
            {
                Sidebar[i] = fresh;
            }
            else
            {
                Sidebar.Add(fresh);
            }
        }
        while (Sidebar.Count > items.Count) Sidebar.RemoveAt(Sidebar.Count - 1);

        if (keep is not null && !Sidebar.Contains(keep))
        {
            var replacement = Sidebar.FirstOrDefault(s => SameEntry(s, keep));
            SelectedSidebarItem = replacement ?? Sidebar.FirstOrDefault(s => s.IsSelectable);
        }
        if (SelectedSidebarItem?.FolderId is long folderId)
        {
            var folder = _mail.GetFolder(folderId);
            FolderWarning = folder?.MassDeleteWarning == true
                ? "Auf dem Server fehlen plötzlich sehr viele Mails dieses Ordners. Zur Sicherheit hat Wisp lokal nichts entfernt."
                : null;
        }
    }

    static bool SameEntry(SidebarItem a, SidebarItem b) =>
        a.Kind == b.Kind && a.AccountId == b.AccountId && a.FolderId == b.FolderId;

    void ReloadMessages()
    {
        if (IsOutboxSelected)
        {
            Messages.Clear();
            Outbox.Clear();
            foreach (var item in _mail.GetOutbox())
            {
                var (state, retry) = item.State switch
                {
                    OutboxState.Queued => (item.Attempts > 0 ? "Wartet auf Verbindung" : "Wird gesendet …", false),
                    OutboxState.Sending => ("Wird gesendet …", false),
                    OutboxState.Accepted => ("Gesendet", false),
                    OutboxState.Failed => ("Nicht gesendet", true),
                    _ => ("Unklar, ob gesendet", true),
                };
                Outbox.Add(new OutboxEntry(item.Id, item.Summary.To, item.Summary.Subject, state, item.LastError,
                    retry, item.State != OutboxState.Sending));
            }
            IsListEmpty = Outbox.Count == 0;
            return;
        }

        if (SelectedSidebarItem?.FolderId is not long folderId)
        {
            Messages.Clear();
            IsListEmpty = true;
            return;
        }

        var fresh = _mail.ListMessages(folderId, SearchText);
        var existing = Messages.ToDictionary(m => m.Id);
        for (var i = 0; i < fresh.Count; i++)
        {
            var source = fresh[i];
            if (i < Messages.Count && Messages[i].Id == source.Id)
            {
                Messages[i].Update(source);
                continue;
            }
            if (existing.TryGetValue(source.Id, out var item))
            {
                var oldIndex = Messages.IndexOf(item);
                if (oldIndex >= 0) Messages.Move(oldIndex, i);
                item.Update(source);
            }
            else
            {
                Messages.Insert(i, new MessageItem(source));
            }
        }
        while (Messages.Count > fresh.Count) Messages.RemoveAt(Messages.Count - 1);
        IsListEmpty = Messages.Count == 0;

        if (CurrentMessageId is long current && _mail.GetMessage(current) is { } m)
        {
            CurrentIsUnread = !m.IsSeen;
            CurrentIsFlagged = m.IsFlagged;
        }
    }

    void ReloadMoveTargets()
    {
        MoveTargets.Clear();
        if (SelectedSidebarItem is not { Kind: SidebarKind.Folder } current) return;
        foreach (var folder in Sidebar.Where(s => s.Kind == SidebarKind.Folder && s.AccountId == current.AccountId && s.FolderId != current.FolderId))
            MoveTargets.Add(folder);
    }

    void UpdateStatus()
    {
        var accounts = _mail.GetAccounts();
        if (accounts.Count == 0)
        {
            StatusKind = "offline";
            StatusText = "Kein Konto eingerichtet";
            return;
        }
        var states = accounts.Select(a => (a, s: _mail.GetStatus(a.Id))).ToList();
        var pending = states.Sum(x => x.s.PendingChanges) + states.Sum(x => x.s.Outbox);
        var suffix = pending > 0 ? $" · {pending} {(pending == 1 ? "Änderung wartet" : "Änderungen warten")}" : "";

        if (states.FirstOrDefault(x => x.s.State is ConnectionState.AuthFailed or ConnectionState.Error) is { a: not null } problem)
        {
            StatusKind = "error";
            StatusText = $"{problem.a.Config.Email}: {problem.s.Text}";
        }
        else if (states.All(x => x.s.State == ConnectionState.Offline))
        {
            StatusKind = "offline";
            StatusText = "Offline" + suffix;
        }
        else if (states.Any(x => x.s.State is ConnectionState.Degraded or ConnectionState.Connecting))
        {
            StatusKind = "degraded";
            StatusText = (states.Any(x => x.s.State == ConnectionState.Degraded) ? "Verbindung instabil" : "Verbinde …") + suffix;
        }
        else
        {
            StatusKind = states.Any(x => x.s.IsSyncing) ? "syncing" : "online";
            StatusText = (states.Any(x => x.s.IsSyncing) ? "Synchronisiere …" : "Verbunden") + suffix;
        }
    }

    void ShowNotice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ───────────────────────── Selection & reading ─────────────────────────

    public async void SetSelection(IReadOnlyList<long> ids)
    {
        _selection = ids;
        HasSelection = ids.Count > 0;
        if (ids.Count != 1)
        {
            ClearReader();
            HasSelection = ids.Count > 0;
            return;
        }
        await OpenAsync(ids[0], showHtml: null, allowImages: null);
    }

    /// <summary>Opens a message. Null means "use the setting"; true is a one-off choice for this mail only.</summary>
    async Task OpenAsync(long messageId, bool? showHtml, bool? allowImages)
    {
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        CurrentMessageId = messageId;
        ReaderProblem = null;
        IsLoadingMail = true;

        var result = await _mail.OpenAsync(messageId, showHtml, allowImages, cts.Token);
        if (cts.IsCancellationRequested) return;
        IsLoadingMail = false;

        ShowHtmlForCurrent = result.Mail?.Html is not null;
        CurrentMail = result.Mail;
        ReaderProblem = result.Problem;
        ImagesAllowedForCurrent = result.Mail?.RemoteImagesAllowed ?? false;

        Attachments.Clear();
        Links.Clear();
        if (result.Mail is { } mail)
        {
            foreach (var a in mail.Attachments) Attachments.Add(new AttachmentItem(a.Index, a.FileName, FormatSize(a.Size)));
            foreach (var l in mail.Links.Where(l => l.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                Links.Add(new LinkItem($"[{l.Index + 1}]", l.Url, l.Suspicious));
        }
        if (_mail.GetMessage(messageId) is { } item)
        {
            CurrentIsUnread = !item.IsSeen;
            CurrentIsFlagged = item.IsFlagged;
        }
    }

    void ClearReader()
    {
        _openCts?.Cancel();
        CurrentMail = null;
        CurrentMessageId = null;
        ReaderProblem = null;
        IsLoadingMail = false;
        HasSelection = false;
        Attachments.Clear();
        Links.Clear();
    }

    static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    IReadOnlyCollection<long> Targets => _selection.Count > 0 ? _selection : CurrentMessageId is long id ? [id] : [];

    // ───────────────────────── Commands ─────────────────────────

    [RelayCommand]
    void Compose()
    {
        var account = SelectedSidebarItem?.AccountId ?? _mail.GetAccounts().FirstOrDefault()?.Id;
        if (account is long id) _dialogs.OpenCompose(new Draft { AccountId = id });
    }

    [RelayCommand]
    void Reply() => StartReply(all: false);

    [RelayCommand]
    void ReplyAll() => StartReply(all: true);

    void StartReply(bool all)
    {
        if (CurrentMail is not { } mail || CurrentMessageId is not long id || _mail.GetMessage(id) is not { } item) return;
        var own = _mail.GetAccounts().First(a => a.Id == item.AccountId).Config.Email;
        _dialogs.OpenCompose(Composer.Reply(mail, item.AccountId, own, all, id));
    }

    [RelayCommand]
    void Forward()
    {
        if (CurrentMail is not { } mail || CurrentMessageId is not long id || _mail.GetMessage(id) is not { } item) return;
        _dialogs.OpenCompose(Composer.Forward(mail, item.AccountId, id));
    }

    [RelayCommand]
    async Task Delete()
    {
        var ids = Targets;
        if (ids.Count == 0) return;
        if (_mail.Delete(ids) == DeleteOutcome.NeedsConfirmation)
        {
            var text = ids.Count == 1 ? "Diese Mail wird endgültig gelöscht – auch auf dem Server." : $"{ids.Count} Mails werden endgültig gelöscht – auch auf dem Server.";
            if (await _dialogs.ConfirmAsync("Endgültig löschen?", text, "Endgültig löschen", danger: true))
                _mail.DeletePermanently(ids);
            else return;
        }
        ClearReader();
        ReloadMessages();
    }

    [RelayCommand]
    void Archive()
    {
        if (Targets.Count > 0 && _mail.Archive(Targets))
        {
            ClearReader();
            ReloadMessages();
        }
    }

    [RelayCommand]
    void MoveTo(SidebarItem? target)
    {
        if (target?.FolderId is not long folderId || Targets.Count == 0) return;
        _mail.Move(Targets, folderId);
        ClearReader();
        ReloadMessages();
    }

    [RelayCommand]
    void ToggleRead()
    {
        if (Targets.Count == 0) return;
        var first = _mail.GetMessage(Targets.First());
        _mail.SetSeen(Targets, first?.IsSeen != true);
        ReloadMessages();
    }

    [RelayCommand]
    void ToggleFlag()
    {
        if (Targets.Count == 0) return;
        var first = _mail.GetMessage(Targets.First());
        _mail.SetFlagged(Targets, first?.IsFlagged != true);
        ReloadMessages();
    }

    [RelayCommand]
    async Task LoadImages()
    {
        if (CurrentMessageId is long id) await OpenAsync(id, showHtml: true, allowImages: true);
    }

    [RelayCommand]
    async Task ShowOriginal()
    {
        if (CurrentMessageId is long id) await OpenAsync(id, showHtml: true, allowImages: ImagesAllowedForCurrent ? true : null);
    }

    [RelayCommand]
    async Task SaveAttachment(AttachmentItem? attachment)
    {
        if (attachment is null || CurrentMessageId is not long id) return;
        await using var stream = await _dialogs.SaveFileAsync(attachment.Name);
        if (stream is null) return;
        await _mail.SaveAttachmentAsync(id, attachment.Index, stream, CancellationToken.None);
        ShowNotice($"„{attachment.Name}“ wurde gespeichert.");
    }

    [RelayCommand]
    Task OpenLink(LinkItem? link) => link is null ? Task.CompletedTask : _dialogs.OpenLinkAsync(link.Url, link.Suspicious);

    public Task OpenLinkUrl(string url, bool suspicious) => _dialogs.OpenLinkAsync(url, suspicious);

    [RelayCommand]
    void Refresh() => _mail.Refresh();

    [RelayCommand]
    async Task OpenSettings()
    {
        await _dialogs.OpenSettingsAsync();
        ReloadAll();
        if (CurrentMessageId is long id) await OpenAsync(id, null, null);
    }

    [RelayCommand]
    async Task AddAccount()
    {
        await _dialogs.OpenAccountSetupAsync();
        ReloadAll();
    }

    [RelayCommand]
    void ApproveMassDelete()
    {
        if (SelectedSidebarItem?.FolderId is long folderId) _mail.ApproveMassDelete(folderId);
        FolderWarning = null;
    }

    [RelayCommand]
    void RetrySend(OutboxEntry? entry)
    {
        if (entry is not null) _mail.RetrySend(entry.Id);
    }

    [RelayCommand]
    async Task DiscardSend(OutboxEntry? entry)
    {
        if (entry is null) return;
        if (await _dialogs.ConfirmAsync("Mail verwerfen?", $"„{entry.Subject}“ wird aus dem Postausgang entfernt und nicht gesendet.", "Verwerfen", danger: true))
            _mail.DiscardFromOutbox(entry.Id);
    }

    [RelayCommand]
    void DismissNotice() => Notice = null;
}
