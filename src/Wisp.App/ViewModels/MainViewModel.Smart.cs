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

    [ObservableProperty] public partial string? Summary { get; set; }
    [ObservableProperty] public partial bool IsSummarizing { get; set; }
    [ObservableProperty] public partial bool CanSummarize { get; set; }
    CancellationTokenSource? _summaryCts;

    public bool ShowSummary => Summary is not null || IsSummarizing;
    partial void OnSummaryChanged(string? value) => OnPropertyChanged(nameof(ShowSummary));
    partial void OnIsSummarizingChanged(bool value) => OnPropertyChanged(nameof(ShowSummary));

    bool AiReady => _mail.Ai is { State: AiState.Ready } && _mail.Settings.Smart.AiEnabled;

    void LoadSummary(long? messageId)
    {
        _summaryCts?.Cancel();
        IsSummarizing = false;
        Summary = messageId is long id && _mail.Ai is not null ? _mail.GetSummary(id) : null;
        CanSummarize = messageId is not null && Summary is null && AiReady;
    }

    [RelayCommand]
    async Task Summarize()
    {
        if (CurrentMessageId is not long id || !AiReady) return;
        _summaryCts?.Cancel();
        var cts = _summaryCts = new CancellationTokenSource();
        CanSummarize = false;
        IsSummarizing = true;
        Summary = "";
        try
        {
            await foreach (var piece in _mail.SummarizeAsync(id, cts.Token).WithCancellation(cts.Token))
            {
                if (cts.IsCancellationRequested || CurrentMessageId != id) return;
                Summary += piece;
            }
            if (string.IsNullOrWhiteSpace(Summary)) Summary = null;
            else if (Messages.FirstOrDefault(m => m.Id == id) is { } row) row.Summary = ShortSummary(Summary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Summary = null;
            ShowNotice("Die Zusammenfassung ist fehlgeschlagen. Das KI-Modell konnte nicht gestartet werden.");
        }
        finally
        {
            if (_summaryCts == cts) IsSummarizing = false;
            CanSummarize = CurrentMessageId == id && Summary is null && AiReady;
        }
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
            if (ai.State == AiState.Ready) CanSummarize = CurrentMessageId is not null && Summary is null && AiReady;
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
                if (change.MessageId == CurrentMessageId && !IsSummarizing) LoadSummary(CurrentMessageId);
                break;
        }
    }

    /// <summary>AIEdition: the summary as preview line of a list row; fetched once per row.</summary>
    void LoadListSummary(MessageItem row)
    {
        if (_mail.Ai is null || !row.HasSummary || row.Summary is not null) return;
        row.Summary = ShortSummary(_mail.GetSummary(row.Id));
    }

    /// <summary>One line for the list: whitespace folded, so a summary with line breaks does not stretch the row.</summary>
    static string? ShortSummary(string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? null : string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
