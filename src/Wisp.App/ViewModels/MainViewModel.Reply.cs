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
    }

    /// <summary>Not in drafts, sent mail, spam or the trash – a reply there is almost always a slip.</summary>
    public bool CanQuickReply => CurrentItem is not null && !IsOutboxSelected
                                 && CurrentRole is not (FolderRole.Drafts or FolderRole.Sent or FolderRole.Junk or FolderRole.Trash);

    public string QuickReplyPlaceholder => CurrentItem is { } item ? $"Antwort an {Addressee(item.From)} …" : "";

    /// <summary>The view moves the keyboard back to the list after sending.</summary>
    public event Action? QuickReplySent;

    [RelayCommand(CanExecute = nameof(HasQuickReplyText))]
    async Task SendQuickReply()
    {
        if (CurrentMessageId is not long id) return;
        var text = QuickReplyText.Trim();
        if (text.Length == 0 || await ReplyDraftAsync(id, all: false) is not { } draft) return;

        var compose = _mail.Settings.Compose;
        var sent = draft with
        {
            Body = text + (compose.SignatureEnabled ? Composer.WithSignature(draft.Body, compose.Signature) : draft.Body),
        };
        var result = _mail.Send(sent);
        if (result.Error is not null)
        {
            ShowNotice(result.Error);
            return;
        }
        _quickDrafts.Remove(id);
        if (CurrentMessageId == id) QuickReplyText = "";
        NotifySent(result, sent);
        QuickReplySent?.Invoke();
    }

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
