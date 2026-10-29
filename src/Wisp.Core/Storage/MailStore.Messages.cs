using Microsoft.Data.Sqlite;
using Wisp.Core.Sync;

namespace Wisp.Core.Storage;

/// <summary>Server location of a cached message, used to execute queued operations.</summary>
public sealed record MessageRef(long Id, long FolderId, string FolderPath, uint Uid, uint UidValidity,
    MessageState State, string? MessageIdHeader, long? SrcFolderId, uint? SrcUid, MessageFlags Flags);

public sealed partial class MailStore
{
    // ───────────────────────── Sync state ─────────────────────────

    public IReadOnlyList<LocalMessage> GetSyncState(long folderId) => Read(() => Query("""
        SELECT m.id, m.uid, m.uidvalidity, m.flags, m.state, m.mid_hash,
               EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = m.id)
        FROM messages m WHERE m.folder_id = $f
        """,
        r => new LocalMessage(r.GetInt64(0), (uint)r.GetInt64(1), (uint)r.GetInt64(2), (MessageFlags)r.GetInt32(3),
            (MessageState)r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6)),
        ("$f", folderId)));

    public string? HashMessageId(string? messageId) =>
        string.IsNullOrWhiteSpace(messageId) ? null : _crypto.Hash(messageId.Trim(), "message.mid");

    /// <summary>
    /// Stores messages reported by the server. Known messages get their flags refreshed;
    /// local rows that lost their UID (UIDVALIDITY change, move without UIDPLUS) are re-attached
    /// by Message-ID so their cached content survives. Returns the number of genuinely new messages.
    /// </summary>
    public int StoreServerMessages(long folderId, uint uidValidity, IEnumerable<ServerMessage> messages) => Tx(() =>
    {
        var added = 0;
        foreach (var msg in messages)
        {
            var midHash = HashMessageId(msg.Envelope.MessageId);

            var known = Query("SELECT id, EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = messages.id) FROM messages WHERE folder_id = $f AND uidvalidity = $v AND uid = $u",
                r => (Id: r.GetInt64(0), Pending: r.GetBoolean(1)),
                ("$f", folderId), ("$v", (long)uidValidity), ("$u", (long)msg.Uid)).FirstOrDefault();
            if (known.Id != 0)
            {
                Exec(known.Pending
                        ? "UPDATE messages SET state = 0 WHERE id = $id AND state = 1"
                        : "UPDATE messages SET flags = $fl, state = CASE state WHEN 1 THEN 0 ELSE state END WHERE id = $id",
                    ("$fl", (int)msg.Flags), ("$id", known.Id));
                continue;
            }

            if (midHash is not null)
            {
                var orphan = Scalar("""
                    SELECT id FROM messages
                    WHERE folder_id = $f AND mid_hash = $h AND state != 2 AND src_folder_id IS NULL
                      AND (uid = 0 OR uidvalidity != $v)
                    LIMIT 1
                    """, ("$f", folderId), ("$h", midHash), ("$v", (long)uidValidity));
                if (orphan is long orphanId)
                {
                    Exec("UPDATE messages SET uid = $u, uidvalidity = $v, state = 0 WHERE id = $id",
                        ("$u", (long)msg.Uid), ("$v", (long)uidValidity), ("$id", orphanId));
                    continue;
                }
            }

            Exec("""
                INSERT INTO messages(folder_id, uid, uidvalidity, mid_hash, date, size, flags, has_attachments, envelope)
                VALUES($f, $u, $v, $h, $d, $s, $fl, $a, $e)
                """,
                ("$f", folderId), ("$u", (long)msg.Uid), ("$v", (long)uidValidity), ("$h", midHash),
                ("$d", msg.Date.ToUnixTimeSeconds()), ("$s", msg.Size), ("$fl", (int)msg.Flags),
                ("$a", msg.HasAttachments), ("$e", _crypto.EncryptJson(msg.Envelope, "message.envelope")));
            added++;
        }
        return added;
    });

    /// <summary>Applies server flags, except for messages whose local change has not reached the server yet.</summary>
    public void UpdateServerFlags(long folderId, uint uidValidity, IEnumerable<(uint Uid, MessageFlags Flags)> flags) => Tx(() =>
    {
        foreach (var (uid, value) in flags)
        {
            Exec("""
                UPDATE messages SET flags = $fl
                WHERE folder_id = $f AND uidvalidity = $v AND uid = $u
                  AND NOT EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = messages.id)
                """, ("$fl", (int)value), ("$f", folderId), ("$v", (long)uidValidity), ("$u", (long)uid));
        }
        return 0;
    });

    public void ApplyAbsencePlan(AbsencePlan plan) => Tx(() =>
    {
        foreach (var id in plan.MarkMissing) Exec("UPDATE messages SET state = 1 WHERE id = $id AND state = 0", ("$id", id));
        foreach (var id in plan.Restore) Exec("UPDATE messages SET state = 0 WHERE id = $id AND state = 1", ("$id", id));
        foreach (var id in plan.Remove)
            Exec("DELETE FROM messages WHERE id = $id AND state = 1 AND NOT EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = $id)", ("$id", id));
        return 0;
    });

    /// <summary>The server explicitly reported these UIDs as expunged (EXPUNGE/VANISHED).</summary>
    public void RemoveExpunged(long folderId, uint uidValidity, IEnumerable<uint> uids) => Tx(() =>
    {
        foreach (var uid in uids)
            Exec("""
                DELETE FROM messages WHERE folder_id = $f AND uidvalidity = $v AND uid = $u
                  AND NOT EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = messages.id)
                """, ("$f", folderId), ("$v", (long)uidValidity), ("$u", (long)uid));
        return 0;
    });

    // ───────────────────────── Reading ─────────────────────────

    const string ListColumns = """
        SELECT m.id, m.folder_id, f.account_id, m.envelope, m.date, m.size, m.flags, m.has_attachments,
               EXISTS(SELECT 1 FROM bodies b WHERE b.message_id = m.id),
               EXISTS(SELECT 1 FROM pending_ops o WHERE o.message_id = m.id)
        FROM messages m JOIN folders f ON f.id = m.folder_id
        """;

    MessageListItem ReadListItem(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2),
        _crypto.DecryptJson<Envelope>(Blob(r, 3), "message.envelope"),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)), r.GetInt64(5),
        (MessageFlags)r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9));

    public IReadOnlyList<MessageListItem> ListMessages(long folderId, int limit) => Read(() =>
        Query(ListColumns + " WHERE m.folder_id = $f AND m.state = 0 ORDER BY m.date DESC LIMIT $n", ReadListItem,
            ("$f", folderId), ("$n", limit)));

    public MessageListItem? GetMessage(long id) => Read(() =>
        Query(ListColumns + " WHERE m.id = $id", ReadListItem, ("$id", id)).FirstOrDefault());

    public MessageRef? GetMessageRef(long id) => Read(() => Query("""
        SELECT m.id, m.folder_id, f.path, m.uid, m.uidvalidity, m.state, m.envelope, m.src_folder_id, m.src_uid, m.flags
        FROM messages m JOIN folders f ON f.id = m.folder_id WHERE m.id = $id
        """,
        r => new MessageRef(r.GetInt64(0), r.GetInt64(1), _crypto.DecryptString(Blob(r, 2), "folder.path"),
            (uint)r.GetInt64(3), (uint)r.GetInt64(4), (MessageState)r.GetInt32(5),
            _crypto.DecryptJson<Envelope>(Blob(r, 6), "message.envelope").MessageId,
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : (uint)r.GetInt64(8),
            (MessageFlags)r.GetInt32(9)),
        ("$id", id)).FirstOrDefault());

    // ───────────────────────── Bodies (offline cache) ─────────────────────────

    public byte[]? GetBody(long messageId) => Read(() =>
        Scalar("SELECT raw FROM bodies WHERE message_id = $id", ("$id", messageId)) is byte[] blob
            ? _crypto.Decrypt(blob, "message.body")
            : null);

    public void SaveBody(long messageId, byte[] raw) => Tx(() =>
        Exec("INSERT OR REPLACE INTO bodies(message_id, raw, fetched) SELECT $id, $r, $t WHERE EXISTS(SELECT 1 FROM messages WHERE id = $id)",
            ("$id", messageId), ("$r", _crypto.Encrypt(raw, "message.body")), ("$t", Now())));

    public IReadOnlyList<(long Id, uint Uid)> GetPrefetchCandidates(long folderId, uint uidValidity, DateTimeOffset since, long maxSize, int limit) =>
        Read(() => Query("""
            SELECT m.id, m.uid FROM messages m
            WHERE m.folder_id = $f AND m.uidvalidity = $v AND m.uid > 0 AND m.state = 0
              AND m.date >= $d AND m.size <= $s
              AND NOT EXISTS(SELECT 1 FROM bodies b WHERE b.message_id = m.id)
            ORDER BY m.date DESC LIMIT $n
            """, r => (r.GetInt64(0), (uint)r.GetInt64(1)),
            ("$f", folderId), ("$v", (long)uidValidity), ("$d", since.ToUnixTimeSeconds()), ("$s", maxSize), ("$n", limit)));

    /// <summary>Frees disk space by dropping cached content of old messages. Purely local; headers stay.</summary>
    public int EvictBodies(DateTimeOffset olderThan) => Tx(() => Exec("""
        DELETE FROM bodies WHERE message_id IN (
          SELECT m.id FROM messages m WHERE m.date < $d AND (m.flags & 2) = 0)
        """, ("$d", olderThan.ToUnixTimeSeconds())));

    // ───────────────────────── Local actions → queued operations ─────────────────────────

    public IReadOnlySet<long> SetFlagLocally(IEnumerable<long> messageIds, MessageFlags flag, bool add) => Tx(() =>
    {
        var touchedFolders = new HashSet<long>();
        foreach (var id in messageIds)
        {
            var msg = GetMessageRef(id);
            if (msg is null || msg.State == MessageState.Deleting) continue;
            var updated = add ? msg.Flags | flag : msg.Flags & ~flag;
            if (updated == msg.Flags) continue;

            Exec("UPDATE messages SET flags = $fl WHERE id = $id", ("$fl", (int)updated), ("$id", id));
            touchedFolders.Add(msg.FolderId);

            // An opposite change that has never been tried simply cancels out.
            var opposite = add ? OpKind.RemoveFlags : OpKind.AddFlags;
            var cancel = Query("SELECT id, payload FROM pending_ops WHERE message_id = $m AND kind = $k AND attempts = 0",
                    r => (Id: r.GetInt64(0), Payload: _crypto.DecryptJson<OpPayload>(Blob(r, 1), "op.payload")),
                    ("$m", id), ("$k", (int)opposite))
                .FirstOrDefault(o => o.Payload.Flags == flag);
            if (cancel.Id != 0)
            {
                Exec("DELETE FROM pending_ops WHERE id = $id", ("$id", cancel.Id));
                continue;
            }
            InsertOp(AccountOfFolder(msg.FolderId), add ? OpKind.AddFlags : OpKind.RemoveFlags, id, new OpPayload { Flags = flag });
        }
        foreach (var f in touchedFolders) RecountFolder(f);
        return touchedFolders;
    });

    /// <summary>Moves messages locally and queues the server move. Consecutive moves of the same message are merged.</summary>
    public IReadOnlySet<long> MoveLocally(IEnumerable<long> messageIds, long targetFolderId) => Tx(() =>
    {
        var target = GetFolder(targetFolderId) ?? throw new ArgumentException("Unknown folder.");
        var touched = new HashSet<long> { targetFolderId };
        foreach (var id in messageIds)
        {
            var msg = GetMessageRef(id);
            if (msg is null || msg.FolderId == targetFolderId || msg.State != MessageState.Normal) continue;
            if (AccountOfFolder(msg.FolderId) != target.AccountId) continue; // cross-account moves are not supported
            touched.Add(msg.FolderId);

            // Remember where a mail was deleted from, so "Wiederherstellen" can put it back there.
            if (target.Role == FolderRole.Trash)
                Exec("UPDATE messages SET origin_folder_id = $o WHERE id = $id", ("$o", msg.FolderId), ("$id", id));

            if (msg.SrcFolderId is long srcFolder)
            {
                var op = PendingOpsFor(id).First(o => o.Kind == OpKind.Move);
                if (srcFolder == targetFolderId)
                {
                    // Moved back to where it is on the server: nothing to do there.
                    Exec("DELETE FROM pending_ops WHERE id = $id", ("$id", op.Id));
                    Exec("UPDATE messages SET folder_id = $f, uid = $u, uidvalidity = $v, src_folder_id = NULL, src_uid = NULL WHERE id = $id",
                        ("$f", srcFolder), ("$u", (long)op.Payload.Uid), ("$v", (long)op.Payload.UidValidity), ("$id", id));
                }
                else
                {
                    UpdateOpPayload(op.Id, op.Payload with { TargetPath = target.Path });
                    Exec("UPDATE messages SET folder_id = $f WHERE id = $id", ("$f", targetFolderId), ("$id", id));
                }
                continue;
            }

            InsertOp(target.AccountId, OpKind.Move, id, new OpPayload
            {
                FolderPath = msg.FolderPath,
                UidValidity = msg.UidValidity,
                Uid = msg.Uid,
                TargetPath = target.Path,
                MessageIdHeader = msg.MessageIdHeader,
            });
            Exec("UPDATE messages SET src_folder_id = folder_id, src_uid = uid, folder_id = $f, uid = 0 WHERE id = $id",
                ("$f", targetFolderId), ("$id", id));
        }
        foreach (var f in touched) RecountFolder(f);
        return touched;
    });

    /// <summary>The folder a message in the trash was deleted from, if known and still there.</summary>
    public long? OriginFolderOf(long messageId) => Read(() =>
        Scalar("SELECT m.origin_folder_id FROM messages m JOIN folders f ON f.id = m.origin_folder_id WHERE m.id = $id AND f.present = 1",
            ("$id", messageId)) as long?);

    /// <summary>Permanent deletion (only offered inside Trash). The local row disappears once the server confirms.</summary>
    public IReadOnlySet<long> ExpungeLocally(IEnumerable<long> messageIds) => Tx(() =>
    {
        var touched = new HashSet<long>();
        foreach (var id in messageIds)
        {
            var msg = GetMessageRef(id);
            if (msg is null || msg.State == MessageState.Deleting) continue;
            touched.Add(msg.FolderId);

            var pendingMove = msg.SrcFolderId is null ? null : PendingOpsFor(id).FirstOrDefault(o => o.Kind == OpKind.Move);
            if (pendingMove is not null)
            {
                // The message never left its original folder on the server: delete it right there.
                Exec("UPDATE pending_ops SET kind = $k WHERE id = $id", ("$k", (int)OpKind.Expunge), ("$id", pendingMove.Id));
                UpdateOpPayload(pendingMove.Id, pendingMove.Payload with { TargetPath = null });
            }
            else
            {
                InsertOp(AccountOfFolder(msg.FolderId), OpKind.Expunge, id, new OpPayload
                {
                    FolderPath = msg.FolderPath,
                    UidValidity = msg.UidValidity,
                    Uid = msg.Uid,
                    MessageIdHeader = msg.MessageIdHeader,
                });
            }
            Exec("UPDATE messages SET state = 2 WHERE id = $id", ("$id", id));
        }
        foreach (var f in touched) RecountFolder(f);
        return touched;
    });

    // ───────────────────────── Operation queue ─────────────────────────

    public IReadOnlyList<PendingOp> GetPendingOps(long accountId) => Read(() => Query(
        "SELECT id, account_id, kind, message_id, payload, attempts, next_attempt FROM pending_ops WHERE account_id = $a ORDER BY id",
        r => (Op: ReadOp(r), Next: r.GetInt64(6)), ("$a", accountId))
        .Select(x => x.Op with { }).ToList());

    /// <summary>Operations whose retry time has come. Later operations on the same message wait for earlier ones.</summary>
    public IReadOnlyList<PendingOp> GetDueOps(long accountId) => Read(() =>
    {
        var now = Now();
        var blocked = new HashSet<long>();
        var due = new List<PendingOp>();
        foreach (var (op, next) in Query(
            "SELECT id, account_id, kind, message_id, payload, attempts, next_attempt FROM pending_ops WHERE account_id = $a ORDER BY id",
            r => (ReadOp(r), r.GetInt64(6)), ("$a", accountId)))
        {
            var key = op.MessageId ?? -op.Id;
            if (blocked.Contains(key)) continue;
            if (next > now) { blocked.Add(key); continue; }
            blocked.Add(key); // one operation per message per round, in order
            due.Add(op);
        }
        return due;
    });

    /// <summary>When the next postponed operation becomes due (so the worker does not sleep past it).</summary>
    public DateTimeOffset? NextOpAttempt(long accountId) => Read<DateTimeOffset?>(() =>
        Scalar("SELECT MIN(next_attempt) FROM pending_ops WHERE account_id = $a AND next_attempt > $n", ("$a", accountId), ("$n", Now())) is long t
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : null);

    public int CountPendingOps(long accountId) => Read(() =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pending_ops WHERE account_id = $a", ("$a", accountId))));

    public void CompleteOp(long opId) => Tx(() => Exec("DELETE FROM pending_ops WHERE id = $id", ("$id", opId)));

    public void RetryOpLater(long opId, TimeSpan delay) => Tx(() =>
        Exec("UPDATE pending_ops SET attempts = attempts + 1, next_attempt = $n WHERE id = $id",
            ("$n", Now() + (long)delay.TotalSeconds), ("$id", opId)));

    /// <summary>A move reached the server. <paramref name="newUid"/> is known when the server supports UIDPLUS.</summary>
    public void MarkMoveDone(long messageId, uint targetUidValidity, uint? newUid) => Tx(() =>
    {
        var folderId = Scalar("SELECT folder_id FROM messages WHERE id = $id", ("$id", messageId));
        if (folderId is null) return 0;
        if (newUid is uint uid)
        {
            // The target folder may already have been synced and contain the arrived copy: keep ours (it has the body).
            Exec("DELETE FROM messages WHERE folder_id = $f AND uidvalidity = $v AND uid = $u AND id != $id",
                ("$f", folderId), ("$v", (long)targetUidValidity), ("$u", (long)uid), ("$id", messageId));
        }
        return Exec("UPDATE messages SET uid = $u, uidvalidity = $v, src_folder_id = NULL, src_uid = NULL WHERE id = $id",
            ("$u", (long)(newUid ?? 0)), ("$v", (long)targetUidValidity), ("$id", messageId));
    });

    /// <summary>Puts a message back where the server has it, after its move could not be done.</summary>
    public void RevertMove(long messageId, OpPayload payload, bool sourceMissing) => Tx(() =>
    {
        var row = Query("SELECT folder_id, src_folder_id FROM messages WHERE id = $id", r => (Folder: r.GetInt64(0), Src: r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), ("$id", messageId)).FirstOrDefault();
        if (row.Src is not long src) return 0;
        Exec("UPDATE messages SET folder_id = $f, uid = $u, uidvalidity = $v, src_folder_id = NULL, src_uid = NULL, state = $s WHERE id = $id",
            ("$f", src), ("$u", (long)payload.Uid), ("$v", (long)payload.UidValidity),
            ("$s", (int)(sourceMissing ? MessageState.Missing : MessageState.Normal)), ("$id", messageId));
        RecountFolder(src);
        RecountFolder(row.Folder);
        return 0;
    });

    /// <summary>Only called after the server confirmed the message is gone.</summary>
    public void DeleteMessageConfirmed(long messageId) => Tx(() =>
    {
        var folder = Scalar("SELECT folder_id FROM messages WHERE id = $id", ("$id", messageId));
        Exec("DELETE FROM messages WHERE id = $id", ("$id", messageId));
        if (folder is long f) RecountFolder(f);
        return 0;
    });

    public void CancelDeletion(long messageId) => Tx(() =>
    {
        Exec("UPDATE messages SET state = 0 WHERE id = $id AND state = 2", ("$id", messageId));
        if (Scalar("SELECT folder_id FROM messages WHERE id = $id", ("$id", messageId)) is long f) RecountFolder(f);
        return 0;
    });

    void InsertOp(long accountId, OpKind kind, long? messageId, OpPayload payload) =>
        Exec("INSERT INTO pending_ops(account_id, kind, message_id, payload, created) VALUES($a, $k, $m, $p, $c)",
            ("$a", accountId), ("$k", (int)kind), ("$m", messageId),
            ("$p", _crypto.EncryptJson(payload, "op.payload")), ("$c", Now()));

    void UpdateOpPayload(long opId, OpPayload payload) =>
        Exec("UPDATE pending_ops SET payload = $p WHERE id = $id", ("$p", _crypto.EncryptJson(payload, "op.payload")), ("$id", opId));

    List<PendingOp> PendingOpsFor(long messageId) => Query(
        "SELECT id, account_id, kind, message_id, payload, attempts FROM pending_ops WHERE message_id = $m ORDER BY id",
        ReadOp, ("$m", messageId));

    PendingOp ReadOp(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), (OpKind)r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3),
        _crypto.DecryptJson<OpPayload>(Blob(r, 4), "op.payload"), r.GetInt32(5));

    long AccountOfFolder(long folderId) => Convert.ToInt64(Scalar("SELECT account_id FROM folders WHERE id = $f", ("$f", folderId)));

    // ───────────────────────── Outbox ─────────────────────────

    /// <param name="sendAfter">Earliest send time; until then the user can still take the mail back.</param>
    public long AddToOutbox(long accountId, byte[] raw, OutboxSummary summary, DateTimeOffset? sendAfter = null) => Tx(() =>
        Convert.ToInt64(Scalar("INSERT INTO outbox(account_id, raw, summary, created, next_attempt) VALUES($a, $r, $s, $c, $n) RETURNING id",
            ("$a", accountId), ("$r", _crypto.Encrypt(raw, "outbox.raw")),
            ("$s", _crypto.EncryptJson(summary, "outbox.summary")), ("$c", Now()),
            ("$n", sendAfter?.ToUnixTimeSeconds() ?? 0))));

    /// <summary>
    /// Claims a queued mail for sending. Atomic, so a mail that the user just took back ("Rückgängig")
    /// can never be sent, and a mail that is being sent can no longer be taken back.
    /// </summary>
    public bool TryBeginSending(long id, int attempts) => Tx(() =>
        Exec("UPDATE outbox SET state = $s, attempts = $a, last_error = NULL WHERE id = $id AND state = $q",
            ("$s", (int)OutboxState.Sending), ("$a", attempts), ("$id", id), ("$q", (int)OutboxState.Queued)) == 1);

    /// <summary>Takes back a mail that has not started sending yet. Returns false if it is too late.</summary>
    public bool TryCancelQueued(long id) => Tx(() =>
        Exec("DELETE FROM outbox WHERE id = $id AND state = $q", ("$id", id), ("$q", (int)OutboxState.Queued)) == 1);

    public IReadOnlyList<OutboxItem> GetOutbox(long? accountId = null) => Read(() => Query(
        "SELECT id, account_id, state, summary, created, attempts, next_attempt, last_error FROM outbox" +
        (accountId is null ? "" : " WHERE account_id = $a") + " ORDER BY id",
        r => new OutboxItem(r.GetInt64(0), r.GetInt64(1), (OutboxState)r.GetInt32(2),
            _crypto.DecryptJson<OutboxSummary>(Blob(r, 3), "outbox.summary"),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)), r.GetInt32(5),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)),
            r.IsDBNull(7) ? null : _crypto.DecryptString(Blob(r, 7), "outbox.error")),
        ("$a", accountId)));

    public byte[] GetOutboxRaw(long id) => Read(() =>
        _crypto.Decrypt((byte[])Scalar("SELECT raw FROM outbox WHERE id = $id", ("$id", id))!, "outbox.raw"));

    public void UpdateOutbox(long id, OutboxState state, int attempts, DateTimeOffset nextAttempt, string? error) => Tx(() =>
        Exec("UPDATE outbox SET state = $s, attempts = $a, next_attempt = $n, last_error = $e WHERE id = $id",
            ("$s", (int)state), ("$a", attempts), ("$n", nextAttempt.ToUnixTimeSeconds()),
            ("$e", error is null ? null : _crypto.EncryptString(error, "outbox.error")), ("$id", id)));

    public void DeleteFromOutbox(long id) => Tx(() => Exec("DELETE FROM outbox WHERE id = $id", ("$id", id)));

    /// <summary>After a crash, a message that was "Sending" may or may not have been delivered.</summary>
    public int MarkInterruptedSendsUncertain() => Tx(() =>
        Exec("UPDATE outbox SET state = $u WHERE state = $s", ("$u", (int)OutboxState.Uncertain), ("$s", (int)OutboxState.Sending)));
}
