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

    /// <param name="restored">True when reopening a mail taken back with "Rückgängig": keep it exactly as it was.</param>
    public ComposeViewModel(MailService mail, Draft draft, bool restored = false)
    {
        _mail = mail;
        _origin = draft;
        var compose = mail.Settings.Compose;
        foreach (var account in mail.GetAccounts())
            Accounts.Add(new AccountChoice(account.Id, Label(account.Config)));
        From = Accounts.FirstOrDefault(a => a.Id == draft.AccountId) ?? Accounts.FirstOrDefault();
        To = draft.To;
        Cc = draft.Cc;
        Bcc = draft.Bcc;
        ShowCc = draft.Cc.Length > 0 || draft.Bcc.Length > 0;
        Subject = draft.Subject;
        Body = !restored && compose.SignatureEnabled ? Composer.WithSignature(draft.Body, compose.Signature) : draft.Body;
        _originBody = Body;
        IsForwardingAttachments = draft.ForwardsAttachmentsOf is not null;
        _loading = true;
        IsAdvanced = draft.HtmlSource is not null || (!restored && compose.Advanced);
        _loading = false;
        if (IsAdvanced) HtmlSource = draft.HtmlSource ?? HtmlEditing.TextToSource(Body);
        _originSource = restored ? "" : HtmlSource;
        if (restored)
            foreach (var path in draft.AttachmentPaths) AttachmentPaths.Add(path);
    }

    readonly string _originBody;
    readonly string _originSource;
    readonly bool _loading;
    string? _sourceBeforeLight;
    string? _bodyAfterLight;

    /// <summary>Light: plain text. Advanced: HTML source with formatting buttons and a live preview.</summary>
    [ObservableProperty] public partial bool IsAdvanced { get; set; }
    [ObservableProperty] public partial string HtmlSource { get; set; } = "";

    partial void OnIsAdvancedChanged(bool value)
    {
        if (_loading) return;
        if (value)
        {
            // Back to Advanced without having edited in Light: restore the formatted version.
            HtmlSource = _sourceBeforeLight is not null && Body == _bodyAfterLight
                ? _sourceBeforeLight
                : HtmlEditing.TextToSource(Body);
        }
        else
        {
            _sourceBeforeLight = HtmlSource;
            Body = HtmlEditing.HasMarkup(HtmlSource)
                ? HtmlEditing.HtmlToText(Composer.ToMailHtml(HtmlSource))
                : System.Net.WebUtility.HtmlDecode(HtmlSource);
            _bodyAfterLight = Body;
        }
        _mail.Settings.Compose.Advanced = value;
        try { _mail.SaveSettings(); } catch (IOException) { }
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

    public bool IsDirty => To != _origin.To || Subject != _origin.Subject || AttachmentPaths.Count > 0
                           || (IsAdvanced ? HtmlSource != _originSource : Body != _originBody);

    public bool ConfirmDiscard => _mail.Settings.Compose.ConfirmDiscard;
    public bool CtrlEnterSends => _mail.Settings.Compose.CtrlEnterSends;

    /// <summary>Raised when the message is safely in the outbox and the window may close.</summary>
    public event Action<SendResult, Draft>? Sent;

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
            Body = IsAdvanced ? "" : Body,
            HtmlSource = IsAdvanced ? HtmlSource : null,
            AttachmentPaths = AttachmentPaths.ToList(),
        };
        var result = _mail.Send(draft);
        Error = result.Error;
        if (Error is null) Sent?.Invoke(result, draft);
    }

    static string Label(AccountConfig c) => string.IsNullOrWhiteSpace(c.DisplayName) ? c.Email : $"{c.DisplayName} <{c.Email}>";
}
