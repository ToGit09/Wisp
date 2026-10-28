using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core;
using Wisp.Core.Accounts;
using Wisp.Core.Mail;

namespace Wisp.App.ViewModels;

public sealed record AccountChoice(long Id, string Label);

public sealed partial class ComposeViewModel : ObservableObject
{
    readonly MailService _mail;
    readonly Draft _origin;

    public ComposeViewModel(MailService mail, Draft draft)
    {
        _mail = mail;
        _origin = draft;
        foreach (var account in mail.GetAccounts())
            Accounts.Add(new AccountChoice(account.Id, Label(account.Config)));
        From = Accounts.FirstOrDefault(a => a.Id == draft.AccountId) ?? Accounts.FirstOrDefault();
        To = draft.To;
        Cc = draft.Cc;
        Bcc = draft.Bcc;
        ShowCc = draft.Cc.Length > 0 || draft.Bcc.Length > 0;
        Subject = draft.Subject;
        Body = draft.Body;
        IsForwardingAttachments = draft.ForwardsAttachmentsOf is not null;
    }

    public ObservableCollection<AccountChoice> Accounts { get; } = [];
    public ObservableCollection<string> AttachmentPaths { get; } = [];
    public bool HasMultipleAccounts => Accounts.Count > 1;
    public bool IsForwardingAttachments { get; }

    [ObservableProperty] public partial AccountChoice? From { get; set; }
    [ObservableProperty] public partial string To { get; set; } = "";
    [ObservableProperty] public partial string Cc { get; set; } = "";
    [ObservableProperty] public partial string Bcc { get; set; } = "";
    [ObservableProperty] public partial bool ShowCc { get; set; }
    [ObservableProperty] public partial string Subject { get; set; } = "";
    [ObservableProperty] public partial string Body { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public string WindowTitle => string.IsNullOrWhiteSpace(Subject) ? "Neue Mail" : Subject;

    partial void OnSubjectChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

    public bool IsDirty => To != _origin.To || Subject != _origin.Subject || Body != _origin.Body || AttachmentPaths.Count > 0;

    /// <summary>Raised when the message is safely in the outbox and the window may close.</summary>
    public event Action? Sent;

    [RelayCommand]
    void ToggleCc() => ShowCc = !ShowCc;

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !AttachmentPaths.Contains(p))) AttachmentPaths.Add(path);
    }

    [RelayCommand]
    void RemoveAttachment(string? path)
    {
        if (path is not null) AttachmentPaths.Remove(path);
    }

    [RelayCommand]
    void Send()
    {
        if (From is null)
        {
            Error = "Bitte zuerst ein Konto einrichten.";
            return;
        }
        var draft = _origin with
        {
            AccountId = From.Id,
            To = To.Trim(),
            Cc = ShowCc ? Cc.Trim() : "",
            Bcc = ShowCc ? Bcc.Trim() : "",
            Subject = Subject.Trim(),
            Body = Body,
            AttachmentPaths = AttachmentPaths.ToList(),
        };
        Error = _mail.Send(draft);
        if (Error is null) Sent?.Invoke();
    }

    static string Label(AccountConfig c) => string.IsNullOrWhiteSpace(c.DisplayName) ? c.Email : $"{c.DisplayName} <{c.Email}>";
}
