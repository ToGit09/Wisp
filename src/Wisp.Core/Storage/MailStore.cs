using Microsoft.Data.Sqlite;
using Wisp.Core.Accounts;
using Wisp.Core.Security;

namespace Wisp.Core.Storage;

/// <summary>
/// The local, encrypted mail cache. All content columns are AES-GCM encrypted via <see cref="Crypto"/>;
/// only ids, dates, sizes and flags are plain (needed for sorting). Thread-safe through a single lock.
/// </summary>
public sealed partial class MailStore : IDisposable
{
    const int SchemaVersion = 1;

    readonly SqliteConnection _db;
    readonly Crypto _crypto;
    readonly object _gate = new();
    SqliteTransaction? _tx;

    public MailStore(string path, Crypto crypto)
    {
        _crypto = crypto;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        AppPaths.RestrictFile(path);
        Exec("PRAGMA journal_mode = WAL");
        Exec("PRAGMA synchronous = NORMAL");
        Exec("PRAGMA foreign_keys = ON");
        Exec("PRAGMA secure_delete = ON");
        Migrate();
    }

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version >= SchemaVersion) return;
        Tx(() =>
        {
            Exec("""
                CREATE TABLE accounts(
                  id INTEGER PRIMARY KEY,
                  sort INTEGER NOT NULL DEFAULT 0,
                  config BLOB NOT NULL,
                  secret BLOB NOT NULL);

                CREATE TABLE folders(
                  id INTEGER PRIMARY KEY,
                  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                  path_hash TEXT NOT NULL,
                  path BLOB NOT NULL,
                  name BLOB NOT NULL,
                  depth INTEGER NOT NULL DEFAULT 0,
                  role INTEGER NOT NULL DEFAULT 0,
                  uidvalidity INTEGER NOT NULL DEFAULT 0,
                  uid_high INTEGER NOT NULL DEFAULT 0,
                  uid_low INTEGER NOT NULL DEFAULT 0,
                  highest_modseq INTEGER NOT NULL DEFAULT 0,
                  unread INTEGER NOT NULL DEFAULT 0,
                  total INTEGER NOT NULL DEFAULT 0,
                  sync_enabled INTEGER NOT NULL DEFAULT 1,
                  mass_warning INTEGER NOT NULL DEFAULT 0,
                  mass_approved INTEGER NOT NULL DEFAULT 0,
                  present INTEGER NOT NULL DEFAULT 1,
                  UNIQUE(account_id, path_hash));

                CREATE TABLE messages(
                  id INTEGER PRIMARY KEY,
                  folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                  uid INTEGER NOT NULL,
                  uidvalidity INTEGER NOT NULL,
                  mid_hash TEXT,
                  date INTEGER NOT NULL,
                  size INTEGER NOT NULL DEFAULT 0,
                  flags INTEGER NOT NULL DEFAULT 0,
                  has_attachments INTEGER NOT NULL DEFAULT 0,
                  envelope BLOB NOT NULL,
                  state INTEGER NOT NULL DEFAULT 0,
                  src_folder_id INTEGER,
                  src_uid INTEGER);
                CREATE INDEX ix_messages_list ON messages(folder_id, state, date DESC);
                CREATE UNIQUE INDEX ux_messages_uid ON messages(folder_id, uidvalidity, uid) WHERE uid > 0;
                CREATE INDEX ix_messages_mid ON messages(folder_id, mid_hash);

                CREATE TABLE bodies(
                  message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                  raw BLOB NOT NULL,
                  fetched INTEGER NOT NULL);

                CREATE TABLE pending_ops(
                  id INTEGER PRIMARY KEY,
                  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                  kind INTEGER NOT NULL,
                  message_id INTEGER,
                  payload BLOB NOT NULL,
                  created INTEGER NOT NULL,
                  attempts INTEGER NOT NULL DEFAULT 0,
                  next_attempt INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX ix_ops_account ON pending_ops(account_id, id);
                CREATE INDEX ix_ops_message ON pending_ops(message_id);

                CREATE TABLE outbox(
                  id INTEGER PRIMARY KEY,
                  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                  raw BLOB NOT NULL,
                  summary BLOB NOT NULL,
                  state INTEGER NOT NULL DEFAULT 0,
                  created INTEGER NOT NULL,
                  attempts INTEGER NOT NULL DEFAULT 0,
                  next_attempt INTEGER NOT NULL DEFAULT 0,
                  last_error BLOB);
                """);
            Exec($"PRAGMA user_version = {SchemaVersion}");
            return 0;
        });
    }

    // ───────────────────────── Accounts ─────────────────────────

    public long AddAccount(AccountConfig config, Credentials credentials) => Tx(() =>
    {
        var sort = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(sort), 0) + 1 FROM accounts"));
        return Convert.ToInt64(Scalar("INSERT INTO accounts(sort, config, secret) VALUES($s, $c, $k) RETURNING id",
            ("$s", sort),
            ("$c", _crypto.EncryptJson(config, "account.config")),
            ("$k", _crypto.EncryptJson(credentials, "account.secret"))));
    });

    public IReadOnlyList<AccountRow> GetAccounts() => Read(() =>
        Query("SELECT id, config FROM accounts ORDER BY sort", r =>
            new AccountRow(r.GetInt64(0), _crypto.DecryptJson<AccountConfig>(Blob(r, 1), "account.config"))));

    public AccountRow? GetAccount(long id) => GetAccounts().FirstOrDefault(a => a.Id == id);

    public void UpdateAccount(long id, AccountConfig config) => Tx(() =>
        Exec("UPDATE accounts SET config = $c WHERE id = $id", ("$c", _crypto.EncryptJson(config, "account.config")), ("$id", id)));

    public Credentials GetCredentials(long accountId) => Read(() =>
        _crypto.DecryptJson<Credentials>((byte[])Scalar("SELECT secret FROM accounts WHERE id = $id", ("$id", accountId))!, "account.secret"));

    public void UpdateCredentials(long accountId, Credentials credentials) => Tx(() =>
        Exec("UPDATE accounts SET secret = $k WHERE id = $id", ("$k", _crypto.EncryptJson(credentials, "account.secret")), ("$id", accountId)));

    /// <summary>Removes an account and all its cached data from this device. Never touches the server.</summary>
    public void DeleteAccount(long accountId) => Tx(() => Exec("DELETE FROM accounts WHERE id = $id", ("$id", accountId)));

    // ───────────────────────── Folders ─────────────────────────

    const string FolderColumns =
        "id, account_id, path, name, depth, role, uidvalidity, uid_high, uid_low, highest_modseq, unread, total, sync_enabled, mass_warning, mass_approved, present";

    FolderRow ReadFolder(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        AccountId = r.GetInt64(1),
        Path = _crypto.DecryptString(Blob(r, 2), "folder.path"),
        Name = _crypto.DecryptString(Blob(r, 3), "folder.name"),
        Depth = r.GetInt32(4),
        Role = (FolderRole)r.GetInt32(5),
        UidValidity = (uint)r.GetInt64(6),
        UidHigh = (uint)r.GetInt64(7),
        UidLow = (uint)r.GetInt64(8),
        HighestModSeq = (ulong)r.GetInt64(9),
        Unread = r.GetInt32(10),
        Total = r.GetInt32(11),
        SyncEnabled = r.GetBoolean(12),
        MassDeleteWarning = r.GetBoolean(13),
        MassDeleteApproved = r.GetBoolean(14),
        Present = r.GetBoolean(15),
    };

    public IReadOnlyList<FolderRow> GetFolders(long accountId, bool includeAbsent = false) => Read(() =>
        Query($"SELECT {FolderColumns} FROM folders WHERE account_id = $a {(includeAbsent ? "" : "AND present = 1")}", ReadFolder, ("$a", accountId))
            .OrderBy(f => f.Role == FolderRole.Inbox ? 0 : f.Role == FolderRole.None ? 2 : 1)
            .ThenBy(f => f.Role)
            .ThenBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    public FolderRow? GetFolder(long folderId) => Read(() =>
        Query($"SELECT {FolderColumns} FROM folders WHERE id = $id", ReadFolder, ("$id", folderId)).FirstOrDefault());

    public FolderRow? FindFolder(long accountId, string path) => Read(() =>
        Query($"SELECT {FolderColumns} FROM folders WHERE account_id = $a AND path_hash = $h", ReadFolder,
            ("$a", accountId), ("$h", _crypto.Hash(path, "folder.path"))).FirstOrDefault());

    public FolderRow? FindFolderByRole(long accountId, FolderRole role) =>
        GetFolders(accountId).FirstOrDefault(f => f.Role == role);

    /// <summary>Inserts or refreshes a folder seen in a server listing and marks it present.</summary>
    public long UpsertFolder(long accountId, string path, string name, int depth, FolderRole role, bool syncByDefault) => Tx(() =>
    {
        var hash = _crypto.Hash(path, "folder.path");
        var existing = Scalar("SELECT id FROM folders WHERE account_id = $a AND path_hash = $h", ("$a", accountId), ("$h", hash));
        if (existing is long id)
        {
            Exec("UPDATE folders SET role = $r, depth = $d, present = 1 WHERE id = $id", ("$r", (int)role), ("$d", depth), ("$id", id));
            return id;
        }
        return Convert.ToInt64(Scalar("""
            INSERT INTO folders(account_id, path_hash, path, name, depth, role, sync_enabled)
            VALUES($a, $h, $p, $n, $d, $r, $s) RETURNING id
            """,
            ("$a", accountId), ("$h", hash),
            ("$p", _crypto.EncryptString(path, "folder.path")),
            ("$n", _crypto.EncryptString(name, "folder.name")),
            ("$d", depth), ("$r", (int)role), ("$s", syncByDefault)));
    });

    /// <summary>
    /// Two-phase folder removal after a complete LIST: a folder missing once is hidden,
    /// a folder missing twice in a row is removed together with its cached messages.
    /// </summary>
    public void ApplyFolderListing(long accountId, IReadOnlySet<long> presentFolderIds) => Tx(() =>
    {
        foreach (var folder in Query("SELECT id, present FROM folders WHERE account_id = $a", r => (Id: r.GetInt64(0), Present: r.GetBoolean(1)), ("$a", accountId)))
        {
            if (presentFolderIds.Contains(folder.Id)) continue;
            // Never drop a folder that still has unsent work attached to it.
            var busy = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM messages m JOIN pending_ops o ON o.message_id = m.id WHERE m.folder_id = $f", ("$f", folder.Id))) > 0;
            if (busy) continue;
            if (folder.Present) Exec("UPDATE folders SET present = 0 WHERE id = $f", ("$f", folder.Id));
            else Exec("DELETE FROM folders WHERE id = $f", ("$f", folder.Id));
        }
        return 0;
    });

    public void UpdateFolderSyncState(long folderId, uint uidValidity, uint uidHigh, uint uidLow, ulong highestModSeq) => Tx(() =>
        Exec("UPDATE folders SET uidvalidity = $v, uid_high = $h, uid_low = $l, highest_modseq = $m WHERE id = $id",
            ("$v", (long)uidValidity), ("$h", (long)uidHigh), ("$l", (long)uidLow), ("$m", (long)highestModSeq), ("$id", folderId)));

    public void SetFolderSyncEnabled(long folderId, bool enabled) => Tx(() =>
        Exec("UPDATE folders SET sync_enabled = $e WHERE id = $id", ("$e", enabled), ("$id", folderId)));

    public void SetMassDeleteWarning(long folderId, bool warning) => Tx(() =>
        Exec("UPDATE folders SET mass_warning = $w, mass_approved = 0 WHERE id = $id", ("$w", warning), ("$id", folderId)));

    /// <summary>The user confirmed that the many missing messages really were deleted elsewhere.</summary>
    public void ApproveMassDelete(long folderId) => Tx(() =>
        Exec("UPDATE folders SET mass_approved = 1 WHERE id = $id", ("$id", folderId)));

    public void RecountFolder(long folderId) => Tx(() => Exec("""
        UPDATE folders SET
          total = (SELECT COUNT(*) FROM messages WHERE folder_id = $f AND state = 0),
          unread = (SELECT COUNT(*) FROM messages WHERE folder_id = $f AND state = 0 AND (flags & 1) = 0)
        WHERE id = $f
        """, ("$f", folderId)));

    // ───────────────────────── helpers ─────────────────────────

    T Read<T>(Func<T> action)
    {
        lock (_gate) return action();
    }

    T Tx<T>(Func<T> action)
    {
        lock (_gate)
        {
            if (_tx is not null) return action(); // nested call joins the outer transaction
            using var tx = _db.BeginTransaction();
            _tx = tx;
            try
            {
                var result = action();
                tx.Commit();
                return result;
            }
            finally
            {
                _tx = null;
            }
        }
    }

    SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    int Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    static byte[] Blob(SqliteDataReader r, int ordinal) => (byte[])r.GetValue(ordinal);

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
