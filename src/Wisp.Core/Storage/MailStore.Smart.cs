using Wisp.Core.Smart;

namespace Wisp.Core.Storage;

/// <summary>A mail waiting to be sorted. <paramref name="AiCategory"/>: the local model's earlier verdict, if any.</summary>
public sealed record ClassifyCandidate(long Id, long AccountId, Envelope Envelope, MailSignals Signals, MessageFlags Flags, DateTimeOffset Date, ClassifiedBy By,
    MailCategory? AiCategory = null);

/// <summary>A fresh, unread inbox mail that may contain a one-time code.</summary>
public sealed record CodeCandidate(long Id, long AccountId, Envelope Envelope, DateTimeOffset Date, bool HasBody);

/// <summary>Wisp 2: storage for the sorting helpers (categories, codes, AI summaries).</summary>
public sealed partial class MailStore
{
    // ───────────────────────── Header signals ─────────────────────────

    /// <summary>Messages (by UID) whose header facts were never read, e.g. stored by Wisp 1.</summary>
    public IReadOnlyList<uint> GetUnsignalledUids(long folderId, uint uidValidity, int limit) => Read(() => Query(
        "SELECT uid FROM messages WHERE folder_id = $f AND uidvalidity = $v AND uid > 0 AND signals = 0 AND state = 0 ORDER BY date DESC LIMIT $n",
        r => (uint)r.GetInt64(0), ("$f", folderId), ("$v", (long)uidValidity), ("$n", limit)));

    public void SetSignals(long folderId, uint uidValidity, IEnumerable<(uint Uid, MailSignals Signals)> signals) => Tx(() =>
    {
        foreach (var (uid, value) in signals)
            Exec("UPDATE messages SET signals = $s, classified = 0 WHERE folder_id = $f AND uidvalidity = $v AND uid = $u",
                ("$s", (int)(value | MailSignals.Checked)), ("$f", folderId), ("$v", (long)uidValidity), ("$u", (long)uid));
        return 0;
    });

    // ───────────────────────── Categories ─────────────────────────

    /// <summary>Inbox mails that still need sorting, newest first.</summary>
    public IReadOnlyList<ClassifyCandidate> GetClassifyQueue(int limit) => Read(() => Query("""
        SELECT m.id, f.account_id, m.envelope, m.signals, m.flags, m.date, m.classified, m.ai_category
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE f.role = 1 AND m.state = 0 AND m.classified = 0 AND m.signals != 0
        ORDER BY m.date DESC LIMIT $n
        """, ReadCandidate, ("$n", limit)));

    ClassifyCandidate ReadCandidate(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), _crypto.DecryptJson<Envelope>(Blob(r, 2), "message.envelope"),
        (MailSignals)r.GetInt32(3), (MessageFlags)r.GetInt32(4), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)), (ClassifiedBy)r.GetInt32(6),
        r.IsDBNull(7) || r.GetInt32(7) < 0 ? null : (MailCategory)r.GetInt32(7));

    /// <summary>Remembers the model's verdict (null: it had none), so sorting again never needs the model again.</summary>
    public void SetAiCategory(long messageId, MailCategory? category) => Tx(() =>
        Exec("UPDATE messages SET ai_category = $c WHERE id = $id", ("$c", category is { } c ? (int)c : -1), ("$id", messageId)));

    public void SetClassification(IEnumerable<(long Id, MailCategory Category, ClassifiedBy By)> results) => Tx(() =>
    {
        foreach (var (id, category, by) in results)
            Exec("UPDATE messages SET category = $c, classified = $b WHERE id = $id", ("$c", (int)category), ("$b", (int)by), ("$id", id));
        return 0;
    });

    /// <summary>Sort every inbox mail again (rules changed, a setting was switched). AI results are kept unless asked.</summary>
    public int ResetClassification(long? accountId = null, bool includeAi = false) => Tx(() => Exec($"""
        UPDATE messages SET classified = 0
        WHERE classified {(includeAi ? "!= 0" : "= 1")}
          AND folder_id IN (SELECT id FROM folders WHERE role = 1 {(accountId is null ? "" : "AND account_id = $a")})
        """, ("$a", accountId)));

    /// <summary>Unread mails per tab of one folder, for the small numbers on the tabs.</summary>
    public IReadOnlyDictionary<MailCategory, int> CountUnreadByCategory(long folderId) => Read(() =>
    {
        var counts = Query("""
            SELECT CASE WHEN (flags & 2) != 0 THEN 1 ELSE category END, COUNT(*)
            FROM messages WHERE folder_id = $f AND state = 0 AND (flags & 1) = 0 GROUP BY 1
            """, r => ((MailCategory)r.GetInt32(0), r.GetInt32(1)), ("$f", folderId));
        return counts.ToDictionary(c => c.Item1, c => c.Item2);
    });

    /// <summary>
    /// Examples for the word filter: the spam folders (spam) and read inbox/archive mails (wanted).
    /// Only subject, sender and preview are used; they stay in memory.
    /// </summary>
    public IReadOnlyList<(Envelope Envelope, MailSignals Signals, bool Spam)> GetTrainingSamples(int perKind) => Read(() =>
    {
        var spam = Query("""
            SELECT m.envelope, m.signals FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role = 5 AND m.state = 0 ORDER BY m.date DESC LIMIT $n
            """, r => (_crypto.DecryptJson<Envelope>(Blob(r, 0), "message.envelope"), (MailSignals)r.GetInt32(1), true), ("$n", perKind));
        var ham = Query("""
            SELECT m.envelope, m.signals FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role IN (1, 6) AND m.state = 0 AND (m.flags & 1) != 0 AND m.category != 3 ORDER BY m.date DESC LIMIT $n
            """, r => (_crypto.DecryptJson<Envelope>(Blob(r, 0), "message.envelope"), (MailSignals)r.GetInt32(1), false), ("$n", perKind));
        return spam.Concat(ham).ToList();
    });

    /// <summary>A number that changes whenever the spam folders change (to know when to retrain).</summary>
    public long TrainingStamp() => Read(() => Convert.ToInt64(Scalar(
        "SELECT COALESCE(SUM(m.id), 0) + COUNT(*) FROM messages m JOIN folders f ON f.id = m.folder_id WHERE f.role = 5 AND m.state = 0")));

    /// <summary>Everyone the user wrote to (recipients in the sent folders). Lower-case addresses.</summary>
    public IReadOnlySet<string> GetContacts(long accountId) => Read(() =>
    {
        var contacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var envelope in Query("""
            SELECT m.envelope FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.account_id = $a AND f.role = 2 ORDER BY m.date DESC LIMIT 3000
            """, r => _crypto.DecryptJson<Envelope>(Blob(r, 0), "message.envelope"), ("$a", accountId)))
        {
            foreach (var address in AddressesIn(envelope.To + "," + envelope.Cc)) contacts.Add(address);
        }
        return contacts;
    });

    /// <summary>Plain addresses from "Anna &lt;anna@x.de&gt;, bob@y.org".</summary>
    internal static IEnumerable<string> AddressesIn(string list)
    {
        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            var lt = p.LastIndexOf('<');
            var gt = p.LastIndexOf('>');
            var address = lt >= 0 && gt > lt ? p[(lt + 1)..gt] : p;
            address = address.Trim().Trim('"').ToLowerInvariant();
            if (address.Contains('@')) yield return address;
        }
    }

    // ───────────────────────── Sender rules ("immer als Werbung einordnen") ─────────────────────────

    public string HashSender(string address) => _crypto.Hash(address.Trim().ToLowerInvariant(), "sender");

    /// <summary>Sets (or with null removes) the user's rule for one sender. Their inbox mails are sorted again.</summary>
    public void SetSenderRule(long accountId, string address, MailCategory? category) => Tx(() =>
    {
        var hash = HashSender(address);
        if (category is { } c)
            Exec("INSERT OR REPLACE INTO sender_rules(account_id, sender_hash, category) VALUES($a, $h, $c)", ("$a", accountId), ("$h", hash), ("$c", (int)c));
        else
            Exec("DELETE FROM sender_rules WHERE account_id = $a AND sender_hash = $h", ("$a", accountId), ("$h", hash));
        return 0;
    });

    public IReadOnlyDictionary<string, MailCategory> GetSenderRules(long accountId) => Read(() =>
        Query("SELECT sender_hash, category FROM sender_rules WHERE account_id = $a", r => (r.GetString(0), (MailCategory)r.GetInt32(1)), ("$a", accountId))
            .ToDictionary(x => x.Item1, x => x.Item2));

    public int CountSenderRules() => Read(() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sender_rules")));

    public void ClearSenderRules() => Tx(() => Exec("DELETE FROM sender_rules"));

    // ───────────────────────── One-time codes ─────────────────────────

    public IReadOnlyList<CodeCandidate> GetCodeCandidates(DateTimeOffset since) => Read(() => Query("""
        SELECT m.id, f.account_id, m.envelope, m.date, EXISTS(SELECT 1 FROM bodies b WHERE b.message_id = m.id)
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE f.role = 1 AND m.state = 0 AND m.code_state = 0 AND (m.flags & 1) = 0 AND m.date >= $d
        ORDER BY m.date DESC LIMIT 20
        """,
        r => new CodeCandidate(r.GetInt64(0), r.GetInt64(1), _crypto.DecryptJson<Envelope>(Blob(r, 2), "message.envelope"),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), r.GetBoolean(4)),
        ("$d", since.ToUnixTimeSeconds())));

    /// <summary>Done with this mail (code found, or certainly none). Older mails are marked in bulk.</summary>
    public void MarkCodeChecked(long messageId) => Tx(() => Exec("UPDATE messages SET code_state = 1 WHERE id = $id", ("$id", messageId)));

    public void MarkCodesCheckedBefore(DateTimeOffset before) => Tx(() =>
        Exec("UPDATE messages SET code_state = 1 WHERE code_state = 0 AND date < $d", ("$d", before.ToUnixTimeSeconds())));

    // ───────────────────────── AI summaries ─────────────────────────

    public string? GetSummary(long messageId) => Read(() =>
        Scalar("SELECT summary FROM messages WHERE id = $id", ("$id", messageId)) is byte[] blob
            ? _crypto.DecryptString(blob, "message.summary")
            : null);

    public void SaveSummary(long messageId, string summary) => Tx(() =>
        Exec("UPDATE messages SET summary = $s WHERE id = $id", ("$s", _crypto.EncryptString(summary, "message.summary")), ("$id", messageId)));

    public int ClearSummaries() => Tx(() => Exec("UPDATE messages SET summary = NULL WHERE summary IS NOT NULL"));

    /// <summary>New inbox mails for the AI: content is cached, not yet handled by the AI, newest first.</summary>
    public IReadOnlyList<ClassifyCandidate> GetAiQueue(DateTimeOffset since, int limit) => Read(() => Query("""
        SELECT m.id, f.account_id, m.envelope, m.signals, m.flags, m.date, m.classified, m.ai_category
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE f.role = 1 AND m.state = 0 AND m.classified != 0 AND m.ai_category IS NULL AND m.date >= $d
          AND EXISTS(SELECT 1 FROM bodies b WHERE b.message_id = m.id)
        ORDER BY m.date DESC LIMIT $n
        """, ReadCandidate, ("$d", since.ToUnixTimeSeconds()), ("$n", limit)));

    public (MailSignals Signals, MailCategory Category) GetSortingInfo(long messageId) => Read(() => Query(
        "SELECT signals, category FROM messages WHERE id = $id", r => ((MailSignals)r.GetInt32(0), (MailCategory)r.GetInt32(1)), ("$id", messageId))
        .FirstOrDefault());
}
