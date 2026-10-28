using MailKit;
using Wisp.Core.Storage;
using KitFlags = MailKit.MessageFlags;
using MessageFlags = Wisp.Core.Storage.MessageFlags;

namespace Wisp.Core.Mail;

static class ImapMapping
{
    public static MessageFlags ToLocal(KitFlags? flags)
    {
        var f = flags ?? KitFlags.None;
        var result = MessageFlags.None;
        if (f.HasFlag(KitFlags.Seen)) result |= MessageFlags.Seen;
        if (f.HasFlag(KitFlags.Flagged)) result |= MessageFlags.Flagged;
        if (f.HasFlag(KitFlags.Answered)) result |= MessageFlags.Answered;
        if (f.HasFlag(KitFlags.Draft)) result |= MessageFlags.Draft;
        return result;
    }

    public static KitFlags ToServer(MessageFlags flags)
    {
        var result = KitFlags.None;
        if (flags.HasFlag(MessageFlags.Seen)) result |= KitFlags.Seen;
        if (flags.HasFlag(MessageFlags.Flagged)) result |= KitFlags.Flagged;
        if (flags.HasFlag(MessageFlags.Answered)) result |= KitFlags.Answered;
        if (flags.HasFlag(MessageFlags.Draft)) result |= KitFlags.Draft;
        return result;
    }

    static readonly (FolderAttributes Attribute, FolderRole Role)[] SpecialUse =
    [
        (FolderAttributes.Inbox, FolderRole.Inbox),
        (FolderAttributes.Sent, FolderRole.Sent),
        (FolderAttributes.Drafts, FolderRole.Drafts),
        (FolderAttributes.Trash, FolderRole.Trash),
        (FolderAttributes.Junk, FolderRole.Junk),
        (FolderAttributes.Archive, FolderRole.Archive),
        (FolderAttributes.All, FolderRole.All),
        (FolderAttributes.Flagged, FolderRole.Flagged),
    ];

    // Fallback for servers without SPECIAL-USE (RFC 6154).
    static readonly Dictionary<string, FolderRole> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = FolderRole.Sent, ["Sent Items"] = FolderRole.Sent, ["Sent Messages"] = FolderRole.Sent,
        ["Gesendet"] = FolderRole.Sent, ["Gesendete Objekte"] = FolderRole.Sent, ["Gesendete Elemente"] = FolderRole.Sent,
        ["Drafts"] = FolderRole.Drafts, ["Entwürfe"] = FolderRole.Drafts,
        ["Trash"] = FolderRole.Trash, ["Deleted"] = FolderRole.Trash, ["Deleted Items"] = FolderRole.Trash,
        ["Deleted Messages"] = FolderRole.Trash, ["Papierkorb"] = FolderRole.Trash, ["Gelöscht"] = FolderRole.Trash,
        ["Gelöschte Elemente"] = FolderRole.Trash, ["Gelöschte Objekte"] = FolderRole.Trash,
        ["Junk"] = FolderRole.Junk, ["Spam"] = FolderRole.Junk, ["Junk-E-Mail"] = FolderRole.Junk, ["Junk E-Mail"] = FolderRole.Junk,
        ["Spamverdacht"] = FolderRole.Junk,
        ["Archive"] = FolderRole.Archive, ["Archiv"] = FolderRole.Archive,
    };

    public static FolderRole RoleOf(IMailFolder folder)
    {
        if (folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return FolderRole.Inbox;
        foreach (var (attribute, role) in SpecialUse)
            if (folder.Attributes.HasFlag(attribute)) return role;
        var isTopLevel = folder.ParentFolder is null || string.IsNullOrEmpty(folder.ParentFolder.FullName)
                         || folder.ParentFolder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
        return isTopLevel && KnownNames.TryGetValue(folder.Name, out var known) ? known : FolderRole.None;
    }
}
