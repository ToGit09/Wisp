using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core.Smart;
using Wisp.Core.Storage;
using Wisp.Core.Sync;

namespace Wisp.App.ViewModels;

/// <summary>A tab above the inbox: "Persönlich", "Rechnungen", "Werbung", "Spamverdacht", "Alle".</summary>
public sealed partial class CategoryTab(string label, MailCategory? category, string icon) : ObservableObject
{
    public string Label { get; } = label;
    public MailCategory? Category { get; } = category;
    public Avalonia.Media.Geometry? Icon => SidebarItem.Find(icon);
    [ObservableProperty] public partial int Unread { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
    partial void OnUnreadChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
    }
}

/// <summary>Wisp 2: inbox tabs, one-time codes and AI summaries.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<CategoryTab> CategoryTabs { get; } =
    [
        new("Persönlich", MailCategory.Personal, "IconPerson"),
        new("Rechnungen", MailCategory.Invoice, "IconReceipt"),
        new("Werbung", MailCategory.Promotion, "IconTag"),
        new("Spamverdacht", MailCategory.SuspectedSpam, "IconJunk"),
        new("Alle", null, "IconInbox") { IsSelected = true },
    ];

    MailCategory? SelectedCategory => ShowCategoryTabs ? CategoryTabs.FirstOrDefault(t => t.IsSelected)?.Category : null;

    /// <summary>Tabs only above an inbox, and only when sorting is switched on.</summary>
    public bool ShowCategoryTabs => _mail.Settings.Smart.Categories && SelectedSidebarItem is { Kind: SidebarKind.Folder, Role: FolderRole.Inbox };

    public bool ShowSortMenu => ShowCategoryTabs;

    [RelayCommand]
    void SelectCategory(CategoryTab? tab)
    {
        if (tab is null || tab.IsSelected) return;
        foreach (var t in CategoryTabs) t.IsSelected = t == tab;
        Messages.Clear(); // a different list opens at the top
        ClearReader();
        ReloadMessages();
    }

    void ResetCategory()
    {
        foreach (var t in CategoryTabs) t.IsSelected = t.Category is null;
        OnPropertyChanged(nameof(ShowCategoryTabs));
        OnPropertyChanged(nameof(ShowSortMenu));
    }

    void UpdateCategoryCounts()
    {
        if (!ShowCategoryTabs || SelectedSidebarItem?.FolderId is not long folderId) return;
        var counts = _mail.CountUnreadByCategory(folderId);
        foreach (var tab in CategoryTabs)
            tab.Unread = tab.Category is { } c ? counts.GetValueOrDefault(c) : counts.Values.Sum();
    }

    /// <summary>Context menu: every mail of this sender goes to this tab from now on (parameter: category name or "none").</summary>
    [RelayCommand]
    void SortSender(string? parameter)
    {
        if (CurrentMessageId is not long id) return;
        MailCategory? category = Enum.TryParse<MailCategory>(parameter, out var c) ? c : null;
        _mail.SetSenderCategory(id, category);
        ShowNotice(category switch
        {
            MailCategory.Personal => "Mails dieses Absenders landen ab jetzt unter „Persönlich“.",
            MailCategory.Invoice => "Mails dieses Absenders landen ab jetzt unter „Rechnungen“.",
            MailCategory.Promotion => "Mails dieses Absenders landen ab jetzt unter „Werbung“.",
            MailCategory.SuspectedSpam => "Mails dieses Absenders landen ab jetzt unter „Spamverdacht“.",
            _ => "Mails dieses Absenders werden wieder automatisch eingeordnet.",
        });
    }

    // ───────────────────────── One-time codes ─────────────────────────

    async void OnCodeFound(string code)
    {
        if (!_mail.Settings.Smart.CopyCodes) return;
        try { await _dialogs.CopySecretAsync(code); }
        catch (Exception)
        {
            ShowNotice($"Einmalcode: {code}");
            return;
        }
        ShowNotice($"Einmalcode {code} wurde kopiert – einfach einfügen.", "Löschen",
            () => _ = _dialogs.ClearClipboardIfAsync(code), TimeSpan.FromSeconds(20));

        var clearAfter = _mail.Settings.Smart.ClearCodeAfterSeconds;
        if (clearAfter > 0)
        {
            DispatcherTimer.RunOnce(() => _ = _dialogs.ClearClipboardIfAsync(code), TimeSpan.FromSeconds(clearAfter));
        }
    }

    // ───────────────────────── AI summaries ─────────────────────────

    /// <summary>
    /// A summary being written. It belongs to its mail, not to the reader: opening another mail lets it run on in
    /// the background, and when it is done it is stored (encrypted, like all mail content). Coming back shows the
    /// text written so far. The model writes one at a time; a second request waits for its turn.
    /// </summary>
    sealed class SummaryJob(long id)
    {
        public long Id { get; } = id;
        public System.Text.StringBuilder Text { get; } = new();
    }

    readonly Dictionary<long, SummaryJob> _summaryJobs = [];

    /// <summary>The open mail's summary: stored, or written so far. Null while nothing is there yet.</summary>
    [ObservableProperty] public partial string? Summary { get; set; }
    [ObservableProperty] public partial bool IsSummarizing { get; set; }
    [ObservableProperty] public partial bool CanSummarize { get; set; }

    /// <summary>
    /// Changes only when the card really appears or goes – not with every word the model writes (the view fades
    /// the card in on this; firing it per word made the card flicker while it filled).
    /// </summary>
    public bool ShowSummary
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Before the first word: what the card says instead of an empty line.</summary>
    public string? SummaryWaitText { get; private set; }

    partial void OnSummaryChanged(string? value) => UpdateSummaryCard();
    partial void OnIsSummarizingChanged(bool value) => UpdateSummaryCard();

    void UpdateSummaryCard()
    {
        ShowSummary = Summary is not null || IsSummarizing;
        var waiting = IsSummarizing && Summary is null;
        var queued = waiting && CurrentMessageId is long id && _summaryJobs.Keys.Any(other => other != id);
        var wait = !waiting ? null : queued ? "Wartet, bis die andere Zusammenfassung fertig ist …" : "Wird geschrieben …";
        if (wait == SummaryWaitText) return;
        SummaryWaitText = wait;
        OnPropertyChanged(nameof(SummaryWaitText));
    }

    bool AiReady => _mail.Ai is { State: AiState.Ready } && _mail.Settings.Smart.AiEnabled;

    void LoadSummary(long? messageId)
    {
        if (messageId is long id && _summaryJobs.TryGetValue(id, out var job))
        {
            IsSummarizing = true;
            Summary = Tidy(job.Text);
        }
        else
        {
            IsSummarizing = false;
            Summary = messageId is long m && _mail.Ai is not null ? _mail.GetSummary(m) : null;
        }
        UpdateSummaryCard();
        CanSummarize = messageId is not null && Summary is null && !IsSummarizing && AiReady;
    }

    [RelayCommand]
    void Summarize()
    {
        if (CurrentMessageId is not long id || !AiReady || _summaryJobs.ContainsKey(id)) return;
        var job = new SummaryJob(id);
        _summaryJobs[id] = job;
        if (Messages.FirstOrDefault(m => m.Id == id) is { } row) row.IsSummarizing = true;
        LoadSummary(id);
        _ = WriteSummaryAsync(job);
    }

    /// <summary>Runs until the summary is complete, whichever mail is open meanwhile.</summary>
    async Task WriteSummaryAsync(SummaryJob job)
    {
        var failed = false;
        try
        {
            // Not cancelled when another mail is opened: SmartService stores the summary once it is complete.
            await foreach (var piece in _mail.SummarizeAsync(job.Id, CancellationToken.None))
            {
                job.Text.Append(piece);
                if (CurrentMessageId == job.Id) Summary = Tidy(job.Text);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            failed = true;
        }
        _summaryJobs.Remove(job.Id);

        var stored = _mail.GetSummary(job.Id);
        if (Messages.FirstOrDefault(m => m.Id == job.Id) is { } row)
        {
            row.IsSummarizing = false;
            row.Summary = ShortSummary(stored);
        }
        if (CurrentMessageId == job.Id) LoadSummary(job.Id);
        else if (IsSummarizing) UpdateSummaryCard(); // a summary waiting for this one may start now
        if (failed) ShowNotice("Die Zusammenfassung ist fehlgeschlagen. Das KI-Modell konnte nicht gestartet werden.");
    }

    /// <summary>The model sometimes starts with a line break; nothing written yet is null.</summary>
    static string? Tidy(System.Text.StringBuilder text)
    {
        var s = text.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>A problem with the model (AIEdition), shown above the list until dismissed.</summary>
    [ObservableProperty] public partial string? AiNotice { get; set; }
    /// <summary>First start of the AIEdition: the model downloads; a slim progress line above the list.</summary>
    [ObservableProperty] public partial bool IsAiDownloading { get; set; }
    [ObservableProperty] public partial double AiProgress { get; set; }
    public string AiProgressText => $"KI-Modell wird geladen · {AiProgress * 100:0} %";
    partial void OnAiProgressChanged(double value) => OnPropertyChanged(nameof(AiProgressText));

    void HookAi()
    {
        if (_mail.Ai is not { } ai) return;
        void Update()
        {
            IsAiDownloading = ai.State == AiState.Downloading;
            AiProgress = Math.Clamp(ai.Progress, 0, 1);
            AiNotice = ai.State == AiState.Error ? ai.StatusText : null;
            if (ai.State == AiState.Ready) CanSummarize = CurrentMessageId is not null && Summary is null && !IsSummarizing && AiReady;
        }
        ai.StateChanged += () => Dispatcher.UIThread.Post(Update);
        Update();
    }

    [RelayCommand]
    void DismissAiNotice() => AiNotice = null;

    void OnSmartChange(WorkerChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Code when change.Text is { } code:
                OnCodeFound(code);
                break;
            case ChangeKind.Summary:
                // A summary written in the background shows up in the list at once, not with the next refresh.
                if (change.MessageId is long id && Messages.FirstOrDefault(m => m.Id == id) is { } row && _mail.Ai is not null)
                    row.Summary = ShortSummary(_mail.GetSummary(id));
                if (change.MessageId is long done && done == CurrentMessageId && !_summaryJobs.ContainsKey(done)) LoadSummary(done);
                break;
        }
    }

    /// <summary>AIEdition: the summary as preview line of a list row; fetched once per row.</summary>
    void LoadListSummary(MessageItem row)
    {
        row.IsSummarizing = _summaryJobs.ContainsKey(row.Id); // rows are new after a folder switch
        if (_mail.Ai is null || !row.HasSummary || row.Summary is not null) return;
        row.Summary = ShortSummary(_mail.GetSummary(row.Id));
    }

    /// <summary>One line for the list: whitespace folded, so a summary with line breaks does not stretch the row.</summary>
    static string? ShortSummary(string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? null : string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
