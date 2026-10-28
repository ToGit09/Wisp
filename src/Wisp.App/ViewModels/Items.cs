using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wisp.Core.Storage;

namespace Wisp.App.ViewModels;

public enum SidebarKind { Account, Folder, Outbox }

public sealed partial class SidebarItem : ObservableObject
{
    public required SidebarKind Kind { get; init; }
    public long AccountId { get; init; }
    public long? FolderId { get; init; }
    public FolderRole Role { get; init; }
    public int Depth { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial int Unread { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    public bool IsHeader => Kind == SidebarKind.Account;
    public bool IsSelectable => !IsHeader;
    public bool HasUnread => Unread > 0;
    public Thickness Indent => new(IsHeader ? 4 : 0 + Depth * 14, IsHeader ? 14 : 0, 0, IsHeader ? 4 : 0);

    public Geometry? Icon => Kind == SidebarKind.Outbox ? Find("IconClock") : Role switch
    {
        FolderRole.Inbox => Find("IconInbox"),
        FolderRole.Sent => Find("IconSent"),
        FolderRole.Drafts => Find("IconDraft"),
        FolderRole.Trash => Find("IconTrash"),
        FolderRole.Junk => Find("IconJunk"),
        FolderRole.Archive => Find("IconArchive"),
        FolderRole.All => Find("IconAll"),
        FolderRole.Flagged => Find("IconStar"),
        _ => Find("IconFolder"),
    };

    partial void OnUnreadChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    public static string DisplayName(FolderRow folder) => folder.Role switch
    {
        FolderRole.Inbox => "Posteingang",
        FolderRole.Sent => "Gesendet",
        FolderRole.Drafts => "Entwürfe",
        FolderRole.Trash => "Papierkorb",
        FolderRole.Junk => "Spam",
        FolderRole.Archive => "Archiv",
        FolderRole.All => "Alle Nachrichten",
        FolderRole.Flagged => "Markiert",
        _ => folder.Name,
    };

    internal static Geometry? Find(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
}

public sealed partial class MessageItem : ObservableObject
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public MessageItem(MessageListItem source) => Update(source);

    public long Id { get; private set; }
    public long AccountId { get; private set; }
    public long FolderId { get; private set; }

    [ObservableProperty] public partial string From { get; set; } = "";
    [ObservableProperty] public partial string Subject { get; set; } = "";
    [ObservableProperty] public partial string Preview { get; set; } = "";
    [ObservableProperty] public partial string DateText { get; set; } = "";
    [ObservableProperty] public partial bool IsUnread { get; set; }
    [ObservableProperty] public partial bool IsFlagged { get; set; }
    [ObservableProperty] public partial bool HasAttachments { get; set; }
    [ObservableProperty] public partial bool IsPending { get; set; }
    [ObservableProperty] public partial bool IsOffline { get; set; }

    public FontWeight Weight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;

    partial void OnIsUnreadChanged(bool value) => OnPropertyChanged(nameof(Weight));

    public void Update(MessageListItem m)
    {
        Id = m.Id;
        AccountId = m.AccountId;
        FolderId = m.FolderId;
        From = m.Envelope.FromDisplay;
        Subject = string.IsNullOrWhiteSpace(m.Envelope.Subject) ? "(kein Betreff)" : m.Envelope.Subject;
        Preview = m.Envelope.Preview;
        DateText = FormatDate(m.Date);
        IsUnread = !m.IsSeen;
        IsFlagged = m.IsFlagged;
        HasAttachments = m.HasAttachments;
        IsPending = m.IsPending;
        IsOffline = m.HasBody;
    }

    public static string FormatDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return local.ToString("HH:mm", German);
        if (local.Date == now.Date.AddDays(-1)) return "Gestern";
        if (local.Year == now.Year) return local.ToString("d. MMM", German);
        return local.ToString("dd.MM.yy", German);
    }
}

public sealed record OutboxEntry(long Id, string To, string Subject, string State, string? Error, bool CanRetry, bool CanDiscard);

public sealed record AttachmentItem(int Index, string Name, string SizeText);

public sealed record LinkItem(string Number, string Url, bool Suspicious);
