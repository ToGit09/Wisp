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
    /// <summary>Second line, e.g. the account of a pinned inbox when there are several accounts.</summary>
    public string? Subtitle { get; init; }

    public bool IsHeader => Kind == SidebarKind.Account;
    public bool IsSelectable => !IsHeader;
    public bool HasUnread => Unread > 0;
    // Capped so that sub-folder icons still fit into the collapsed strip.
    public double IndentWidth => Math.Min(Depth, 2) * 10;

    /// <summary>Special folders and the outbox have a symbol; the user's own folders show their initial.</summary>
    public bool HasIcon => Kind == SidebarKind.Outbox || Role != FolderRole.None;

    /// <summary>First letter or digit of the name ("[Gmail]/Reisen" → "R", "2026 Steuer" → "2").</summary>
    public string Initial => Name.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0'
        ? char.ToUpper(c, System.Globalization.CultureInfo.CurrentCulture).ToString()
        : "#";

    // Each name keeps its colour, so "Projekte" and "Privat" are told apart at a glance.
    public IBrush BadgeForeground => new SolidColorBrush(BadgeColors.For(Name));
    public IBrush BadgeBackground => new SolidColorBrush(BadgeColors.For(Name), 0.16);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(BadgeForeground));
        OnPropertyChanged(nameof(BadgeBackground));
    }

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

    public static string DisplayName(FolderRole role, string name) => role switch
    {
        FolderRole.Inbox => "Posteingang",
        FolderRole.Sent => "Gesendet",
        FolderRole.Drafts => "Entwürfe",
        FolderRole.Trash => "Papierkorb",
        FolderRole.Junk => "Spam",
        FolderRole.Archive => "Archiv",
        FolderRole.All => "Alle Nachrichten",
        FolderRole.Flagged => "Markiert",
        _ => name,
    };

    internal static Geometry? Find(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
}

/// <summary>A stable colour per name (folder initials, sender avatars).</summary>
static class BadgeColors
{
    static readonly Color[] Palette = new[] { "#4F6BED", "#7C5CFA", "#0F9D9A", "#2E9D57", "#E07A1F", "#D64545", "#D6458F", "#5B6170" }
        .Select(Color.Parse).ToArray();

    public static Color For(string text)
    {
        var hash = 17;
        foreach (var c in text.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return Palette[(hash & 0x7FFFFFFF) % Palette.Length];
    }
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

    [ObservableProperty] public partial string Initials { get; set; } = "";
    [ObservableProperty] public partial IBrush? AvatarForeground { get; set; }
    [ObservableProperty] public partial IBrush? AvatarBackground { get; set; }

    /// <summary>"Anna Schmidt" → "AS", "info@paket.example" → "I".</summary>
    static string InitialsOf(string name)
    {
        var words = name.Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != '\0')
            .ToList();
        if (words.Count == 0) return "?";
        var first = char.ToUpperInvariant(words[0]).ToString();
        return name.Contains('@') || words.Count == 1 ? first : first + char.ToUpperInvariant(words[^1]);
    }

    partial void OnIsUnreadChanged(bool value) => OnPropertyChanged(nameof(Weight));
    public void Update(MessageListItem m)
    {
        Id = m.Id;
        AccountId = m.AccountId;
        FolderId = m.FolderId;
        if (From != m.Envelope.FromDisplay || AvatarForeground is null)
        {
            var color = BadgeColors.For(m.Envelope.FromAddress.Length > 0 ? m.Envelope.FromAddress : m.Envelope.FromDisplay);
            Initials = InitialsOf(m.Envelope.FromDisplay);
            AvatarForeground = new SolidColorBrush(color);
            AvatarBackground = new SolidColorBrush(color, 0.16);
        }
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
