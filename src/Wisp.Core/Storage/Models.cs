using Wisp.Core.Accounts;

namespace Wisp.Core.Storage;

[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1,
    Flagged = 2,
    Answered = 4,
    Draft = 8,
}

public enum FolderRole
{
    None = 0,
    Inbox = 1,
    Sent = 2,
    Drafts = 3,
    Trash = 4,
    Junk = 5,
    Archive = 6,
    All = 7,
    Flagged = 8,
}

/// <summary>Local life cycle of a cached message. See docs/SYNC.md.</summary>
public enum MessageState
{
    Normal = 0,
    /// <summary>Absent from one complete server listing. Hidden, but kept until a second listing confirms.</summary>
    Missing = 1,
    /// <summary>Permanent deletion requested by the user, waiting for the server to confirm.</summary>
    Deleting = 2,
}

public enum OpKind
{
    AddFlags = 1,
    RemoveFlags = 2,
    Move = 3,
    Expunge = 4,
}

public enum OutboxState
{
    Queued = 0,
    Sending = 1,
    Accepted = 2,
    Failed = 3,
    /// <summary>Interrupted while sending: the server may or may not have it. Never resent automatically.</summary>
    Uncertain = 4,
}

/// <summary>Decrypted summary of a message as shown in the list. Stored encrypted.</summary>
public sealed record Envelope
{
    public string Subject { get; init; } = "";
    public string FromName { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string To { get; init; } = "";
    public string? Cc { get; init; }
    public string? MessageId { get; init; }
    public string Preview { get; init; } = "";

    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}

public sealed record AccountRow(long Id, AccountConfig Config);

public sealed record FolderRow
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public int Depth { get; init; }
    public FolderRole Role { get; init; }
    public uint UidValidity { get; init; }
    public uint UidHigh { get; init; }
    public uint UidLow { get; init; }
    public ulong HighestModSeq { get; init; }
    public int Unread { get; init; }
    public int Total { get; init; }
    public bool SyncEnabled { get; init; }
    public bool MassDeleteWarning { get; init; }
    public bool MassDeleteApproved { get; init; }
    public bool Present { get; init; }
}

/// <summary>What the sync engine needs to know about a local message.</summary>
public sealed record LocalMessage(long Id, uint Uid, uint UidValidity, MessageFlags Flags, MessageState State, string? MidHash, bool HasPendingOps);

/// <summary>A message as reported by the server, ready to be stored.</summary>
public sealed record ServerMessage(uint Uid, DateTimeOffset Date, long Size, MessageFlags Flags, Envelope Envelope, bool HasAttachments);

public sealed record MessageListItem(
    long Id, long FolderId, long AccountId, Envelope Envelope, DateTimeOffset Date, long Size,
    MessageFlags Flags, bool HasAttachments, bool HasBody, bool IsPending)
{
    public bool IsSeen => Flags.HasFlag(MessageFlags.Seen);
    public bool IsFlagged => Flags.HasFlag(MessageFlags.Flagged);
}

/// <summary>Server coordinates of a message, captured when an operation is queued.</summary>
public sealed record OpPayload
{
    public MessageFlags Flags { get; init; }
    public string? FolderPath { get; init; }
    public uint UidValidity { get; init; }
    public uint Uid { get; init; }
    public string? TargetPath { get; init; }
    public string? MessageIdHeader { get; init; }
}

public sealed record PendingOp(long Id, long AccountId, OpKind Kind, long? MessageId, OpPayload Payload, int Attempts);

public sealed record OutboxSummary(string To, string Subject, string MessageId);

public sealed record OutboxItem(long Id, long AccountId, OutboxState State, OutboxSummary Summary,
    DateTimeOffset Created, int Attempts, DateTimeOffset NextAttempt, string? LastError);
