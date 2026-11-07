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

    /// <summary>What a left/right step did, so the view can move the keyboard along.</summary>
    public enum SideStep { None, Tab, IntoSidebar, OutOfSidebar }

    /// <summary>
    /// Left arrow (or mouse "back"). Inbox and sidebar form one row: [sidebar] [Persönlich] … [Alle].
    /// In the inbox it goes to the previous tab; from the first tab (and in every other folder) into the sidebar,
    /// which opens if it was collapsed.
    /// </summary>
    public SideStep StepLeft()
    {
        if (ShowCategoryTabs && CategoryTabs.FirstOrDefault(t => t.IsSelected) is { } tab && CategoryTabs.IndexOf(tab) is > 0 and var i)
        {
            SelectCategory(CategoryTabs[i - 1]);
            return SideStep.Tab;
        }
        SetSidebarOpen(true);
        return SideStep.IntoSidebar;
    }

    /// <summary>Right arrow (or mouse "forward"): collapses an open sidebar first, then goes to the next tab.</summary>
    public SideStep StepRight()
    {
        if (SidebarAlwaysOpen)
        {
            SetSidebarOpen(false);
            return SideStep.OutOfSidebar;
        }
        if (ShowCategoryTabs && CategoryTabs.FirstOrDefault(t => t.IsSelected) is { } tab
            && CategoryTabs.IndexOf(tab) is var i && i < CategoryTabs.Count - 1)
        {
            SelectCategory(CategoryTabs[i + 1]);
            return SideStep.Tab;
        }
        return SideStep.None;
    }

    /// <summary>Same switch as "Seitenleiste automatisch einklappen" in the settings, so it is remembered.</summary>
    void SetSidebarOpen(bool open)
    {
        if (SidebarAlwaysOpen == open) return;
        _mail.Settings.Appearance.AutoCollapseSidebar = !open;
        try { _mail.SaveSettings(); } catch (IOException) { }
        OnPropertyChanged(nameof(SidebarAlwaysOpen));
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

    /// <summary>"KI-Modell wird geladen …" above the list while the model downloads (AIEdition, first start).</summary>
    [ObservableProperty] public partial string? AiNotice { get; set; }

    void HookAi()
    {
        if (_mail.Ai is not { } ai) return;
        ai.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            AiNotice = ai.State switch
            {
                AiState.Downloading => $"KI-Modell wird geladen ({ai.Progress * 100:0} %). Wisp ist währenddessen normal nutzbar.",
                AiState.Error => ai.StatusText,
                _ => null,
            };
            if (ai.State == AiState.Ready) CanSummarize = CurrentMessageId is not null && Summary is null && AiReady;
        });
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
            case ChangeKind.Summary when change.MessageId == CurrentMessageId && !IsSummarizing:
                LoadSummary(CurrentMessageId);
                break;
        }
    }
}
