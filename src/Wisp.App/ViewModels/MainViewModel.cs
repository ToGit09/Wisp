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
            NoticeActionLabel = null;
            _noticeAction = null;
        });
        _mail.Changed += OnChanged;
        ReloadSidebar();
        UpdateStatus();
        SelectedSidebarItem = Pinned.FirstOrDefault() ?? Sidebar.FirstOrDefault();
    }

    /// <summary>All accounts, for the switcher. The sidebar shows the folders of the active one only.</summary>
    public ObservableCollection<AccountItem> Accounts { get; } = [];
    [ObservableProperty] public partial AccountItem? ActiveAccount { get; set; }
    /// <summary>Another account has unread mail in its inbox: a dot on the switcher, so it is not missed.</summary>
    [ObservableProperty] public partial bool OtherAccountsUnread { get; set; }

    /// <summary>Inbox of the active account, pinned above the scrolling folder list.</summary>
    public ObservableCollection<SidebarItem> Pinned { get; } = [];
    public ObservableCollection<SidebarItem> Sidebar { get; } = [];
    IEnumerable<SidebarItem> AllSidebar => Pinned.Concat(Sidebar);
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<OutboxEntry> Outbox { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public ObservableCollection<LinkItem> Links { get; } = [];
    public ObservableCollection<SidebarItem> MoveTargets { get; } = [];

    public bool HasAccounts => _mail.GetAccounts().Count > 0;
    public bool ShowPreview => _mail.Settings.Appearance.ShowPreview;
    public bool ShowAvatars => _mail.Settings.Appearance.ShowAvatars;
    public bool SidebarAlwaysOpen => !_mail.Settings.Appearance.AutoCollapseSidebar;

    [ObservableProperty] public partial string WindowTitle { get; set; } = "Wisp";
    /// <summary>Optional button in the toast, e.g. "Rückgängig" after sending.</summary>
    [ObservableProperty] public partial string? NoticeActionLabel { get; set; }
    Action? _noticeAction;

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

    public bool ShowQuickActions => _mail.Settings.Appearance.QuickActions;

    // Quick actions depend on the folder: Spam → "Kein Spam"/Löschen, Entwürfe → Bearbeiten/Verwerfen.
    FolderRole CurrentRole => SelectedSidebarItem?.Role ?? FolderRole.None;
    public bool QuickJunk => CurrentRole == FolderRole.Junk;
    public bool QuickDrafts => CurrentRole == FolderRole.Drafts;
    public bool QuickTrash => CurrentRole == FolderRole.Trash;
    public bool QuickDefault => !QuickJunk && !QuickDrafts && !QuickTrash;
    public bool QuickTwoButtons => !QuickDefault;
    public bool ShowContextMenu => _mail.Settings.Appearance.ContextMenu;

    public bool ShowRemoteImageBanner => CurrentMail is { HasRemoteImages: true, RemoteImagesAllowed: false } && ShowHtmlForCurrent;
    public bool ShowHtmlBanner => CurrentMail is { HasHtml: true } && !ShowHtmlForCurrent;
    public bool CanArchive => SelectedSidebarItem is { } s && _mail.ArchiveFolder(s.AccountId) is { } a && a.Id != s.FolderId;

    /// <summary>"An: … · Cc: …" in one line of the reader header.</summary>
    public string Recipients => CurrentMail is not { } mail ? ""
        : mail.Cc is { Length: > 0 } cc ? $"An: {mail.To}  ·  Cc: {cc}"
        : $"An: {mail.To}";

    partial void OnCurrentMailChanged(RenderedMail? value)
    {
        OnPropertyChanged(nameof(Recipients));
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
        IsOutboxSelected = value.Kind == SidebarKind.Outbox;
        FolderTitle = value.Name;
        SearchText = "";
        if (value.FolderId is long folderId) _mail.FolderOpened(folderId);
        // A different folder starts empty, so the list opens at the top (newest mail), never somewhere below.
        Messages.Clear();
        ReloadMessages();
        ReloadMoveTargets();
        ClearReader();
        OnPropertyChanged(nameof(CanArchive));
        OnPropertyChanged(nameof(QuickJunk));
        OnPropertyChanged(nameof(QuickDrafts));
        OnPropertyChanged(nameof(QuickTrash));
        OnPropertyChanged(nameof(QuickDefault));
        OnPropertyChanged(nameof(QuickTwoButtons));
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
        OnPropertyChanged(nameof(ShowAvatars));
        OnPropertyChanged(nameof(SidebarAlwaysOpen));
        OnPropertyChanged(nameof(ShowQuickActions));
        OnPropertyChanged(nameof(ShowContextMenu));
        if (SelectedSidebarItem is null || !AllSidebar.Contains(SelectedSidebarItem))
            SelectedSidebarItem = AllSidebar.FirstOrDefault();
    }

    void ReloadSidebar()
    {
        var items = new List<SidebarItem>();
        var pinned = new List<SidebarItem>();
        var switcher = new List<AccountItem>();
        var accounts = _mail.GetAccounts();
        var active = accounts.FirstOrDefault(a => a.Id == _mail.Settings.Appearance.ActiveAccountId) ?? accounts.FirstOrDefault();
        var inboxUnread = 0;
        foreach (var account in accounts)
        {
            var status = _mail.GetStatus(account.Id);
            var folders = _mail.GetFolders(account.Id);
            var unread = folders.FirstOrDefault(f => f.Role == FolderRole.Inbox)?.Unread ?? 0;
            inboxUnread += unread;
            switcher.Add(new AccountItem
            {
                AccountId = account.Id, Email = account.Config.Email, Unread = unread,
                HasProblem = status.State is ConnectionState.AuthFailed or ConnectionState.Error,
                IsActive = account.Id == active?.Id,
            });
            if (account.Id != active?.Id) continue;

            // Special folders appear once per account; any further folder keeps its own, unambiguous name.
            var roles = new HashSet<FolderRole>();
            var names = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var folder in folders)
            {
                var role = folder.Role != FolderRole.None && roles.Add(folder.Role) ? folder.Role : FolderRole.None;
                var name = SidebarItem.DisplayName(role, folder.Name);
                names[name] = names.GetValueOrDefault(name) + 1;
                if (names[name] > 1) name = $"{name} ({names[name]})";
                var item = new SidebarItem
                {
                    Kind = SidebarKind.Folder, AccountId = account.Id, FolderId = folder.Id, Role = role,
                    Depth = role == FolderRole.None ? folder.Depth : 0,
                    Name = name,
                    Unread = role is FolderRole.Sent or FolderRole.Trash or FolderRole.Drafts or FolderRole.All ? 0 : folder.Unread,
                    Status = folder.MassDeleteWarning ? "!" : null,
                };
                if (role == FolderRole.Inbox) pinned.Add(item);
                else items.Add(item);
            }
        }
        var outbox = _mail.GetOutbox();
        if (outbox.Count > 0)
            items.Add(new SidebarItem { Kind = SidebarKind.Outbox, Name = "Postausgang", Unread = outbox.Count });

        SyncAccounts(switcher);
        var keep = SelectedSidebarItem;
        SyncInto(Pinned, pinned);
        SyncInto(Sidebar, items);
        if (keep is not null && !AllSidebar.Contains(keep))
        {
            var replacement = AllSidebar.FirstOrDefault(s => SameEntry(s, keep));
            SelectedSidebarItem = replacement ?? AllSidebar.FirstOrDefault();
        }

        WindowTitle = _mail.Settings.Appearance.UnreadInTitle && inboxUnread > 0 ? $"Wisp ({inboxUnread})" : "Wisp";
        if (SelectedSidebarItem?.FolderId is long folderId)
        {
            var folder = _mail.GetFolder(folderId);
            FolderWarning = folder?.MassDeleteWarning == true
                ? "Auf dem Server fehlen plötzlich sehr viele Mails dieses Ordners. Zur Sicherheit hat Wisp lokal nichts entfernt."
                : null;
        }
    }

    /// <summary>Updates the switcher in place, so an open account list does not flicker on every refresh.</summary>
    void SyncAccounts(List<AccountItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var fresh = items[i];
            if (i < Accounts.Count && Accounts[i].AccountId == fresh.AccountId)
            {
                Accounts[i].Unread = fresh.Unread;
                Accounts[i].HasProblem = fresh.HasProblem;
                Accounts[i].IsActive = fresh.IsActive;
            }
            else if (i < Accounts.Count) Accounts[i] = fresh;
            else Accounts.Add(fresh);
        }
        while (Accounts.Count > items.Count) Accounts.RemoveAt(Accounts.Count - 1);
        ActiveAccount = Accounts.FirstOrDefault(a => a.IsActive);
        OtherAccountsUnread = Accounts.Any(a => !a.IsActive && a.HasUnread);
    }

    static bool SameEntry(SidebarItem a, SidebarItem b) =>
        a.Kind == b.Kind && a.AccountId == b.AccountId && a.FolderId == b.FolderId;

    /// <summary>Updates the list in place where possible, so the selection survives a refresh.</summary>
    static void SyncInto(ObservableCollection<SidebarItem> target, List<SidebarItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var fresh = items[i];
            if (i < target.Count && SameEntry(target[i], fresh))
            {
                target[i].Name = fresh.Name;
                target[i].Unread = fresh.Unread;
                target[i].Status = fresh.Status;
            }
            else if (i < target.Count) target[i] = fresh;
            else target.Add(fresh);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

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
        SyncMessages(fresh);
        IsListEmpty = Messages.Count == 0;

        if (CurrentMessageId is long current && _mail.GetMessage(current) is { } m)
        {
            CurrentIsUnread = !m.IsSeen;
            CurrentIsFlagged = m.IsFlagged;
        }
    }

    /// <summary>
    /// Brings the visible list in line with <paramref name="fresh"/> with as few changes as possible:
    /// removals first, then insertions, moves only if the order really changed. Deleting one mail is
    /// one removal, so the list does not scroll. (Shifting every following row one by one made the list
    /// follow the moved rows down to the very end.)
    /// </summary>
    void SyncMessages(IReadOnlyList<MessageListItem> fresh)
    {
        var freshIds = fresh.Select(m => m.Id).ToHashSet();
        for (var i = Messages.Count - 1; i >= 0; i--)
            if (!freshIds.Contains(Messages[i].Id)) Messages.RemoveAt(i);

        var existing = Messages.Select(m => m.Id).ToHashSet();
        for (var i = 0; i < fresh.Count; i++)
        {
            var source = fresh[i];
            if (i < Messages.Count && Messages[i].Id == source.Id)
            {
                Messages[i].Update(source);
                continue;
            }
            if (existing.Contains(source.Id))
            {
                // Order changed (rare, e.g. a date correction): move this one row into place.
                var oldIndex = IndexOf(source.Id, i);
                Messages.Move(oldIndex, i);
                Messages[i].Update(source);
            }
            else
            {
                Messages.Insert(i, new MessageItem(source));
            }
        }
        while (Messages.Count > fresh.Count) Messages.RemoveAt(Messages.Count - 1);
    }

    int IndexOf(long id, int from)
    {
        for (var j = from; j < Messages.Count; j++)
            if (Messages[j].Id == id) return j;
        return Messages.IndexOf(Messages.First(m => m.Id == id));
    }

    void ReloadMoveTargets()
    {
        MoveTargets.Clear();
        if (SelectedSidebarItem is not { Kind: SidebarKind.Folder } current) return;
        foreach (var folder in AllSidebar.Where(s => s.Kind == SidebarKind.Folder && s.AccountId == current.AccountId && s.FolderId != current.FolderId))
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

    public void ShowNotice(string? text) => ShowNotice(text, null, null, TimeSpan.FromSeconds(8));

    /// <summary>Shows a toast, optionally with one action button (e.g. "Rückgängig").</summary>
    public void ShowNotice(string? text, string? actionLabel, Action? action, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Notice = text;
        NoticeActionLabel = actionLabel;
        _noticeAction = action;
        _noticeTimer.Stop();
        _noticeTimer.Interval = duration;
        _noticeTimer.Start();
    }

    [RelayCommand]
    void NoticeAction()
    {
        var action = _noticeAction;
        Notice = null;
        NoticeActionLabel = null;
        _noticeAction = null;
        action?.Invoke();
    }

    // ───────────────────────── Selection & reading ─────────────────────────

    /// <summary>
    /// Started by double-clicking a mail: from then on a click adds or removes mails, so several can be
    /// deleted or moved at once without Strg/Umschalt. Ends with "Fertig", Esc, or when nothing is selected.
    /// </summary>
    [ObservableProperty] public partial bool IsSelectionMode { get; set; }
    [ObservableProperty] public partial int SelectionCount { get; set; }
    public string SelectionText => SelectionCount == 1 ? "1 Mail ausgewählt" : $"{SelectionCount} Mails ausgewählt";
    partial void OnSelectionCountChanged(int value) => OnPropertyChanged(nameof(SelectionText));

    [RelayCommand]
    void EndSelection() => IsSelectionMode = false;

    public async void SetSelection(IReadOnlyList<long> ids)
    {
        _selection = ids;
        SelectionCount = ids.Count;
        if (ids.Count == 0) IsSelectionMode = false;
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
        var account = ActiveAccount?.AccountId ?? _mail.GetAccounts().FirstOrDefault()?.Id;
        if (account is long id) _dialogs.OpenCompose(new Draft { AccountId = id });
    }

    /// <summary>Shows another account's folders in the sidebar and opens its inbox.</summary>
    [RelayCommand]
    void SwitchAccount(AccountItem? account)
    {
        if (account is null || account.AccountId == ActiveAccount?.AccountId) return;
        ActivateAccount(account.AccountId);
        ReloadSidebar();
        SelectedSidebarItem = Pinned.FirstOrDefault() ?? Sidebar.FirstOrDefault();
    }

    void ActivateAccount(long accountId)
    {
        _mail.Settings.Appearance.ActiveAccountId = accountId;
        try { _mail.SaveSettings(); } catch (IOException) { }
    }

    [RelayCommand]
    Task Reply() => StartReplyAsync(all: false);

    [RelayCommand]
    Task ReplyAll() => StartReplyAsync(all: true);

    async Task StartReplyAsync(bool all)
    {
        if (CurrentMessageId is not long id || _mail.GetMessage(id) is not { } item) return;
        // The quick-action button can be pressed while the mail is still loading: wait for it instead of doing nothing.
        var mail = CurrentMail ?? (await _mail.OpenAsync(id, showHtml: false, allowRemoteImages: false, CancellationToken.None)).Mail;
        if (mail is null)
        {
            ShowNotice("Die Mail ist noch nicht geladen. Bitte gleich noch einmal versuchen.");
            return;
        }
        var own = _mail.GetAccounts().First(a => a.Id == item.AccountId).Config.Email;
        _dialogs.OpenCompose(Composer.Reply(mail, item.AccountId, own, all, id, quote: _mail.Settings.Compose.QuoteOnReply));
    }

    /// <summary>Spam folder: the mail is wanted after all → back to the inbox (with a way back).</summary>
    [RelayCommand]
    void NotSpam()
    {
        var ids = Targets.ToList();
        if (ids.Count == 0 || SelectedSidebarItem is not { FolderId: long junk } current) return;
        if (Pinned.FirstOrDefault(p => p.AccountId == current.AccountId)?.FolderId is not long inbox) return;
        _mail.Move(ids, inbox);
        ClearReader();
        ReloadMessages();
        ShowNotice(ids.Count == 1 ? "In den Posteingang verschoben." : $"{ids.Count} Mails in den Posteingang verschoben.", "Rückgängig", () =>
        {
            _mail.Move(ids, junk);
            ReloadMessages();
        }, TimeSpan.FromSeconds(10));
    }

    public bool CanMarkSpam => SelectedSidebarItem is { } s && JunkFolder(s.AccountId) is { } junk && junk != s.FolderId;

    long? JunkFolder(long accountId) =>
        AllSidebar.FirstOrDefault(f => f.AccountId == accountId && f.Role == FolderRole.Junk)?.FolderId;

    /// <summary>Moves mails into the spam folder (with a way back).</summary>
    [RelayCommand]
    void MarkSpam()
    {
        var ids = Targets.ToList();
        if (ids.Count == 0 || SelectedSidebarItem is not { FolderId: long from } current) return;
        if (JunkFolder(current.AccountId) is not long junk || junk == from) return;
        _mail.Move(ids, junk);
        ClearReader();
        ReloadMessages();
        ShowNotice(ids.Count == 1 ? "Als Spam markiert." : $"{ids.Count} Mails als Spam markiert.", "Rückgängig", () =>
        {
            _mail.Move(ids, from);
            ReloadMessages();
        }, TimeSpan.FromSeconds(10));
    }

    /// <summary>Trash: put mails back where they were deleted from (inbox if unknown).</summary>
    [RelayCommand]
    void Restore()
    {
        var ids = Targets.ToList();
        if (ids.Count == 0) return;
        var restored = _mail.Restore(ids);
        if (restored == 0) return;
        ClearReader();
        ReloadMessages();
        ShowNotice(restored == 1 ? "Mail wiederhergestellt." : $"{restored} Mails wiederhergestellt.");
    }

    /// <summary>Drafts folder: continue writing. The old draft goes to the trash once the mail is sent.</summary>
    [RelayCommand]
    async Task EditDraft()
    {
        if (CurrentMessageId is not long id || _mail.GetMessage(id) is not { } item) return;
        var mail = CurrentMail ?? (await _mail.OpenAsync(id, showHtml: false, allowRemoteImages: false, CancellationToken.None)).Mail;
        if (mail is null)
        {
            ShowNotice("Der Entwurf ist noch nicht geladen. Bitte gleich noch einmal versuchen.");
            return;
        }
        _dialogs.OpenCompose(new Draft
        {
            AccountId = item.AccountId,
            To = mail.To,
            Cc = mail.Cc ?? "",
            Subject = mail.Subject,
            Body = mail.Reply.QuotableText,
            ReplacesDraft = id,
        });
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
        var ids = Targets.ToList();
        if (ids.Count == 0) return;
        var from = SelectedSidebarItem?.FolderId;
        var outcome = _mail.Delete(ids);
        if (outcome == DeleteOutcome.NeedsConfirmation)
        {
            var text = ids.Count == 1 ? "Diese Mail wird endgültig gelöscht – auch auf dem Server." : $"{ids.Count} Mails werden endgültig gelöscht – auch auf dem Server.";
            if (await _dialogs.ConfirmAsync("Endgültig löschen?", text, "Endgültig löschen", danger: true))
                _mail.DeletePermanently(ids);
            else return;
        }
        ClearReader();
        ReloadMessages();

        if (outcome == DeleteOutcome.MovedToTrash && from is long folderId)
        {
            // Visible confirmation plus a way back, until the next action replaces the toast.
            var what = ids.Count == 1 ? "Mail" : $"{ids.Count} Mails";
            ShowNotice($"{what} in den Papierkorb verschoben.", "Rückgängig", () =>
            {
                _mail.Move(ids, folderId);
                ReloadMessages();
            }, TimeSpan.FromSeconds(10));
        }
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
        var known = _mail.GetAccounts().Select(a => a.Id).ToHashSet();
        await _dialogs.OpenAccountSetupAsync();
        // A freshly added account is what the user wants to see next.
        if (_mail.GetAccounts().FirstOrDefault(a => !known.Contains(a.Id)) is { } added) ActivateAccount(added.Id);
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
    void DismissNotice()
    {
        Notice = null;
        NoticeActionLabel = null;
        _noticeAction = null;
    }
}
