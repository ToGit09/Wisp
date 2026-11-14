using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wisp.Core.Storage;

namespace Wisp.App.ViewModels;

public enum SidebarKind { Folder, Outbox }

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
    public IBrush BadgeForeground => BadgeColors.Brush(Name);
    public IBrush BadgeBackground => BadgeColors.SoftBrush(Name);

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

/// <summary>An entry in the account switcher at the top of the sidebar.</summary>
public sealed partial class AccountItem : ObservableObject
{
    public required long AccountId { get; init; }
    public required string Email { get; init; }

    /// <summary>Unread mails in this account's inbox.</summary>
    [ObservableProperty] public partial int Unread { get; set; }
    [ObservableProperty] public partial bool HasProblem { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }

    public bool HasUnread => Unread > 0;
    partial void OnUnreadChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    /// <summary>"anna@example.org" → "A".</summary>
    public string Initial => Email.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0'
        ? char.ToUpperInvariant(c).ToString()
        : "@";

    // Each account keeps its colour, so the active one is recognised at a glance, also in the collapsed sidebar.
    public IBrush BadgeBackground => BadgeColors.Brush(Email);
}

/// <summary>A stable colour per name (folder initials, sender avatars).</summary>
static class BadgeColors
{
    static readonly Color[] Palette = new[] { "#6B4EE6", "#4F6BED", "#0E8F8A", "#2F9A5B", "#D9771F", "#D64550", "#D2468C", "#5E6272" }
        .Select(Color.Parse).ToArray();

    // One shared, immutable brush per colour: a list of 500 mails used to create 1000 brushes on every load.
    static readonly IBrush[] Solid = Palette.Select(c => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c)).ToArray();
    static readonly IBrush[] Soft = Palette.Select(c => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c, 0.16)).ToArray();

    public static IBrush Brush(string text) => Solid[Index(text)];
    public static IBrush SoftBrush(string text) => Soft[Index(text)];
    public static Color For(string text) => Palette[Index(text)];

    static int Index(string text)
    {
        var hash = 17;
        foreach (var c in text.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return (hash & 0x7FFFFFFF) % Palette.Length;
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
    /// <summary>Wisp 2: shown with a warning tag in the list ("Spamverdacht").</summary>
    [ObservableProperty] public partial bool IsSuspect { get; set; }
    [ObservableProperty] public partial bool HasSummary { get; set; }

    /// <summary>When the mail arrived; the reader header shows it at once, before the content is loaded.</summary>
    [ObservableProperty] public partial DateTimeOffset Date { get; set; }
    public string LongDateText => Date.ToLocalTime().ToString("ddd, d. MMM yyyy, HH:mm", German);
    public string FullDateText => Date.ToLocalTime().ToString("dddd, d. MMMM yyyy, HH:mm", German);
    partial void OnDateChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(LongDateText));
        OnPropertyChanged(nameof(FullDateText));
    }

    /// <summary>
    /// AIEdition: the stored summary replaces the first line of text as the preview, so mails can be sorted out
    /// from the list without opening them. Null in the LightEdition and for mails without a summary.
    /// </summary>
    [ObservableProperty] public partial string? Summary { get; set; }
    /// <summary>The model is writing this mail's summary right now – also while another mail is open.</summary>
    [ObservableProperty] public partial bool IsSummarizing { get; set; }
    public bool ShowsSummary => Summary is not null || IsSummarizing;
    public string PreviewText => Summary ?? (IsSummarizing ? "Wird zusammengefasst …" : Preview);
    partial void OnSummaryChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowsSummary));
        OnPropertyChanged(nameof(PreviewText));
    }
    partial void OnIsSummarizingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsSummary));
        OnPropertyChanged(nameof(PreviewText));
    }
    partial void OnPreviewChanged(string value) => OnPropertyChanged(nameof(PreviewText));

    /// <summary>Wisp 2: tab of the mail, shown as a small badge in the corner of the sender initials.</summary>
    [ObservableProperty] public partial Wisp.Core.Smart.MailCategory Category { get; set; }
    public bool HasCategory => Category != Wisp.Core.Smart.MailCategory.None;
    public Geometry? CategoryIcon => Category switch
    {
        Wisp.Core.Smart.MailCategory.Personal => SidebarItem.Find("IconPerson"),
        Wisp.Core.Smart.MailCategory.Invoice => SidebarItem.Find("IconReceipt"),
        Wisp.Core.Smart.MailCategory.Promotion => SidebarItem.Find("IconTag"),
        Wisp.Core.Smart.MailCategory.SuspectedSpam => SidebarItem.Find("IconJunk"),
        _ => null,
    };
    public IBrush? CategoryBrush => Category switch
    {
        Wisp.Core.Smart.MailCategory.Personal => CategoryBrushes.Personal,
        Wisp.Core.Smart.MailCategory.Invoice => CategoryBrushes.Invoice,
        Wisp.Core.Smart.MailCategory.Promotion => CategoryBrushes.Promotion,
        Wisp.Core.Smart.MailCategory.SuspectedSpam => CategoryBrushes.Spam,
        _ => null,
    };
    public string? CategoryName => Category switch
    {
        Wisp.Core.Smart.MailCategory.Personal => "Persönlich",
        Wisp.Core.Smart.MailCategory.Invoice => "Rechnungen",
        Wisp.Core.Smart.MailCategory.Promotion => "Werbung",
        Wisp.Core.Smart.MailCategory.SuspectedSpam => "Spamverdacht",
        _ => null,
    };

    partial void OnCategoryChanged(Wisp.Core.Smart.MailCategory value)
    {
        OnPropertyChanged(nameof(HasCategory));
        OnPropertyChanged(nameof(CategoryIcon));
        OnPropertyChanged(nameof(CategoryBrush));
        OnPropertyChanged(nameof(CategoryName));
    }

    static class CategoryBrushes
    {
        public static readonly IBrush Personal = new SolidColorBrush(Color.Parse("#6B4EE6"));
        public static readonly IBrush Invoice = new SolidColorBrush(Color.Parse("#0E8F8A"));
        public static readonly IBrush Promotion = new SolidColorBrush(Color.Parse("#D9771F"));
        public static readonly IBrush Spam = new SolidColorBrush(Color.Parse("#D64550"));
    }

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
            var key = m.Envelope.FromAddress.Length > 0 ? m.Envelope.FromAddress : m.Envelope.FromDisplay;
            Initials = InitialsOf(m.Envelope.FromDisplay);
            AvatarForeground = BadgeColors.Brush(key);
            AvatarBackground = BadgeColors.SoftBrush(key);
        }
        From = m.Envelope.FromDisplay;
        Subject = string.IsNullOrWhiteSpace(m.Envelope.Subject) ? "(kein Betreff)" : m.Envelope.Subject;
        Preview = m.Envelope.Preview;
        DateText = FormatDate(m.Date);
        Date = m.Date;
        IsUnread = !m.IsSeen;
        IsFlagged = m.IsFlagged;
        HasAttachments = m.HasAttachments;
        IsPending = m.IsPending;
        IsOffline = m.HasBody;
        IsSuspect = m.Category == Wisp.Core.Smart.MailCategory.SuspectedSpam;
        HasSummary = m.HasSummary;
        Category = m.Category;
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

/// <summary>"Später senden": a suggested time, shown as a capsule ("Morgen früh · Di, 8:00").</summary>
public sealed record SendTime(string Label, string Detail, DateTimeOffset At)
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>In an hour, this evening (if still ahead), tomorrow morning, and Monday morning before a weekend.</summary>
    public static IReadOnlyList<SendTime> Suggestions(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var list = new List<SendTime>();

        var hour = local.AddHours(1);
        hour = hour.Date.AddHours(hour.Hour).AddMinutes(Math.Ceiling(hour.Minute / 5.0) * 5); // a round time
        list.Add(new("In einer Stunde", hour.ToString("H:mm", German), new DateTimeOffset(hour)));

        if (local.Hour < 17) list.Add(new("Heute Abend", "18:00", new DateTimeOffset(local.Date.AddHours(18))));

        var tomorrow = local.Date.AddDays(1).AddHours(8);
        list.Add(new("Morgen früh", tomorrow.ToString("ddd, H:mm", German), new DateTimeOffset(tomorrow)));

        if (local.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday)
        {
            var monday = local.Date.AddDays(((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7).AddHours(8);
            list.Add(new("Montag früh", monday.ToString("d. MMM, H:mm", German), new DateTimeOffset(monday)));
        }
        return list;
    }

    /// <summary>"heute um 18:00", "morgen um 8:00", "am Montag um 8:00".</summary>
    public static string WhenText(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        var time = local.ToString("H:mm", German);
        return (local.Date - DateTime.Today).Days switch
        {
            0 => $"heute um {time}",
            1 => $"morgen um {time}",
            < 7 => $"am {local.ToString("dddd", German)} um {time}",
            _ => $"am {local.ToString("d. MMMM", German)} um {time}",
        };
    }
}
