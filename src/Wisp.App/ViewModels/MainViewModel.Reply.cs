using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core.Mail;
using Wisp.Core.Storage;

namespace Wisp.App.ViewModels;

/// <summary>
/// Quick reply: a field under the open mail. Most answers are a line or two, and those should not need a window
/// of their own. Strg+Enter sends (with "Rückgängig" while the mail waits in the outbox); the expand button carries
/// the text over into the full composer. What is typed stays with its mail when another one is opened meanwhile.
/// </summary>
public sealed partial class MainViewModel
{
    readonly Dictionary<long, string> _quickDrafts = [];

    [ObservableProperty] public partial string QuickReplyText { get; set; } = "";

    public bool HasQuickReplyText => !string.IsNullOrWhiteSpace(QuickReplyText);

    partial void OnQuickReplyTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuickReplyText));
        SendQuickReplyCommand.NotifyCanExecuteChanged();
        ShowSendLaterCommand.NotifyCanExecuteChanged();
        SaveQuickReplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Not in drafts, sent mail, spam or the trash – a reply there is almost always a slip.</summary>
    public bool CanQuickReply => CurrentItem is not null && !IsOutboxSelected
                                 && CurrentRole is not (FolderRole.Drafts or FolderRole.Sent or FolderRole.Junk or FolderRole.Trash);

    public string QuickReplyPlaceholder => CurrentItem is { } item ? $"Antwort an {Addressee(item.From)} …" : "";

    /// <summary>The view moves the keyboard back to the list after sending.</summary>
    public event Action? QuickReplySent;

    [RelayCommand(CanExecute = nameof(HasQuickReplyText))]
    Task SendQuickReply() => SendQuickReplyAt(null);

    /// <summary>Sends now (<paramref name="time"/> null) or at the chosen time ("Später senden").</summary>
    [RelayCommand]
    async Task SendQuickReplyAt(SendTime? time)
    {
        CloseSendMenu();
        if (await QuickReplyDraftAsync() is not { } reply) return;
        var (id, text, draft) = reply;
        var result = _mail.Send(draft, time?.At);
        if (result.Error is not null)
        {
            ShowNotice(result.Error);
            return;
        }
        QuickReplyDone(id);
        if (time is null && result.UndoWindow <= TimeSpan.Zero)
        {
            ShowNotice("Die Mail wird gesendet.");
            return;
        }
        // "Rückgängig" puts the text back into the field it came from, not into a window of its own.
        var notice = time is null ? $"Die Mail wird in {result.UndoWindow.TotalSeconds:0} Sekunden gesendet." : $"Die Mail wird {SendTime.WhenText(time.At)} gesendet.";
        ShowNotice(notice, UndoLabel, () =>
        {
            if (result.OutboxId is not long outboxId || !_mail.TryUndoSend(outboxId, draft))
            {
                ShowNotice("Zu spät – die Mail ist bereits unterwegs.");
                return;
            }
            _quickDrafts[id] = text;
            if (CurrentMessageId == id) QuickReplyText = text;
        }, time is null ? result.UndoWindow : TimeSpan.FromSeconds(8));
    }

    /// <summary>Keeps the answer as a draft in the Drafts folder, to finish later (on any device).</summary>
    [RelayCommand(CanExecute = nameof(HasQuickReplyText))]
    async Task SaveQuickReply()
    {
        CloseSendMenu();
        if (await QuickReplyDraftAsync() is not { } reply) return;
        var (id, _, draft) = reply;
        if (_mail.SaveDraft(draft) is { } error)
        {
            ShowNotice(error);
            return;
        }
        QuickReplyDone(id);
        ShowNotice("Als Entwurf gespeichert.");
    }

    /// <summary>The typed answer as a complete reply (signature, quote), or null when there is nothing to send.</summary>
    async Task<(long Id, string Text, Draft Draft)?> QuickReplyDraftAsync()
    {
        if (CurrentMessageId is not long id) return null;
        var text = QuickReplyText.Trim();
        if (text.Length == 0 || await ReplyDraftAsync(id, all: false) is not { } draft) return null;
        var compose = _mail.Settings.Compose;
        return (id, text, draft with
        {
            Body = text + (compose.SignatureEnabled ? Composer.WithSignature(draft.Body, compose.Signature) : draft.Body),
        });
    }

    void QuickReplyDone(long id)
    {
        _quickDrafts.Remove(id);
        if (CurrentMessageId == id) QuickReplyText = "";
        QuickReplySent?.Invoke();
    }

    // ───────────────────────── Send options (right-click on the send button) ─────────────────────────

    /// <summary>Capsules above the send button: "Speichern" and "Später senden".</summary>
    [ObservableProperty] public partial bool IsSendMenuOpen { get; set; }
    /// <summary>Second step of the capsules: the times for "Später senden".</summary>
    [ObservableProperty] public partial bool IsSendLaterOpen { get; set; }
    [ObservableProperty] public partial IReadOnlyList<SendTime> SendTimes { get; set; } = [];

    public void OpenSendMenu()
    {
        if (!CanQuickReply) return;
        SendTimes = SendTime.Suggestions(DateTimeOffset.Now);
        IsSendLaterOpen = false;
        IsSendMenuOpen = true;
    }

    [RelayCommand]
    public void CloseSendMenu()
    {
        IsSendLaterOpen = false;
        IsSendMenuOpen = false;
    }

    [RelayCommand(CanExecute = nameof(HasQuickReplyText))]
    void ShowSendLater() => IsSendLaterOpen = true;

    /// <summary>Continue in the full composer (Cc, attachments, formatting) with what is already typed.</summary>
    [RelayCommand]
    async Task ExpandQuickReply()
    {
        if (CurrentMessageId is not long id) return;
        var typed = QuickReplyText.TrimEnd();
        if (await ReplyDraftAsync(id, all: false) is not { } draft) return;
        _quickDrafts.Remove(id);
        if (CurrentMessageId == id) QuickReplyText = "";
        _dialogs.OpenCompose(draft, typed: typed);
    }

    /// <summary>A reply to <paramref name="id"/>; waits for the mail if it is still loading.</summary>
    async Task<Draft?> ReplyDraftAsync(long id, bool all)
    {
        if (_mail.GetMessage(id) is not { } item) return null;
        var mail = CurrentMessageId == id && CurrentMail is { } open
            ? open
            : (await _mail.OpenAsync(id, showHtml: false, allowRemoteImages: false, CancellationToken.None)).Mail;
        if (mail is null)
        {
            ShowNotice("Die Mail ist noch nicht geladen. Bitte gleich noch einmal versuchen.");
            return null;
        }
        var own = _mail.GetAccounts().First(a => a.Id == item.AccountId).Config.Email;
        return Composer.Reply(mail, item.AccountId, own, all, id, quote: _mail.Settings.Compose.QuoteOnReply);
    }

    void StashQuickReply()
    {
        CloseSendMenu();
        if (CurrentMessageId is long id)
        {
            if (HasQuickReplyText) _quickDrafts[id] = QuickReplyText;
            else _quickDrafts.Remove(id);
        }
        QuickReplyText = "";
    }

    void RestoreQuickReply(long id) => QuickReplyText = _quickDrafts.GetValueOrDefault(id) ?? "";

    /// <summary>"Schmidt, Anna" → "Anna Schmidt"; empty → "den Absender".</summary>
    static string Addressee(string from)
    {
        var name = from.Trim().Trim('"').Trim();
        if (name.Length == 0) return "den Absender";
        if (!name.Contains('@') && name.Split(',', 2) is [var family, var given] && given.Trim().Length > 0)
            return $"{given.Trim()} {family.Trim()}";
        return name;
    }
}
