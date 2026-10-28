using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Wisp.Core.Accounts;
using Wisp.Core.Security;
using Wisp.Core.Storage;

namespace Wisp.Core.Tests;

public sealed class StoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("wisp-test-").FullName;
    readonly MailStore _store;
    readonly long _account;
    readonly long _inbox;
    readonly long _trash;

    public StoreTests()
    {
        _store = new MailStore(Path.Combine(_dir, "wisp.db"), new Crypto(RandomNumberGenerator.GetBytes(32)));
        _account = _store.AddAccount(new AccountConfig
        {
            DisplayName = "Erika Mustermann",
            Email = "erika@example.org",
            UserName = "erika@example.org",
            Imap = new ServerSettings("imap.example.org", 993, Accounts.Security.Tls),
            Smtp = new ServerSettings("smtp.example.org", 465, Accounts.Security.Tls),
        }, Credentials.FromPassword("streng-geheim-123"));
        _inbox = _store.UpsertFolder(_account, "INBOX", "INBOX", 0, FolderRole.Inbox, true);
        _trash = _store.UpsertFolder(_account, "Papierkorb", "Papierkorb", 0, FolderRole.Trash, true);
    }

    static ServerMessage Server(uint uid, string subject, string? messageId = null) =>
        new(uid, DateTimeOffset.UtcNow, 1000, MessageFlags.None,
            new Envelope { Subject = subject, FromAddress = "bob@example.org", MessageId = messageId ?? $"{uid}@example.org" }, false);

    [Fact]
    public void Nothing_sensitive_is_stored_in_plain_text()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(1, "Kontoauszug Oktober")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;
        _store.SaveBody(id, Encoding.UTF8.GetBytes("Subject: Kontoauszug Oktober\r\n\r\nIhr Saldo beträgt 1.234,56 EUR"));
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "wisp.db"));
        var wal = Path.Combine(_dir, "wisp.db-wal");
        if (File.Exists(wal)) bytes = [.. bytes, .. File.ReadAllBytes(wal)];
        var content = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("streng-geheim-123", content);
        Assert.DoesNotContain("erika@example.org", content);
        Assert.DoesNotContain("Kontoauszug", content);
        Assert.DoesNotContain("Saldo", content);
        Assert.DoesNotContain("Papierkorb", content);
        Assert.DoesNotContain("bob@example.org", content);
    }

    [Fact]
    public void Moving_offline_keeps_the_message_visible_in_the_target_and_queues_the_move()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;

        _store.MoveLocally([id], _trash);

        Assert.Empty(_store.ListMessages(_inbox, 10));
        Assert.Single(_store.ListMessages(_trash, 10));
        var op = Assert.Single(_store.GetPendingOps(_account));
        Assert.Equal(OpKind.Move, op.Kind);
        Assert.Equal("INBOX", op.Payload.FolderPath);
        Assert.Equal(5u, op.Payload.Uid);
    }

    [Fact]
    public void Moving_back_before_sync_cancels_the_queued_move()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;

        _store.MoveLocally([id], _trash);
        _store.MoveLocally([id], _inbox);

        Assert.Empty(_store.GetPendingOps(_account));
        var state = _store.GetSyncState(_inbox).Single();
        Assert.Equal(5u, state.Uid);
    }

    [Fact]
    public void Moved_message_remembers_where_it_still_is_on_the_server()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;
        _store.MoveLocally([id], _trash);

        // The server still reports UID 5 in INBOX because the move has not been executed yet.
        var local = _store.GetSyncState(_inbox);
        Assert.Empty(local); // the row lives in Trash now …
        Assert.NotNull(_store.GetMessageRef(id)!.SrcUid); // … and remembers where it is on the server
    }

    [Fact]
    public void Toggling_read_twice_offline_leaves_no_pending_work()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;

        _store.SetFlagLocally([id], MessageFlags.Seen, true);
        _store.SetFlagLocally([id], MessageFlags.Seen, false);

        Assert.Empty(_store.GetPendingOps(_account));
    }

    [Fact]
    public void Server_flags_do_not_overwrite_a_local_change_that_is_not_synced_yet()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;
        _store.SetFlagLocally([id], MessageFlags.Seen, true);

        _store.UpdateServerFlags(_inbox, 1, [(5u, MessageFlags.None)]);

        Assert.True(_store.GetMessage(id)!.IsSeen);
    }

    [Fact]
    public void Cached_content_survives_a_uidvalidity_change()
    {
        _store.StoreServerMessages(_inbox, 1, [Server(5, "Hallo", "abc@example.org")]);
        var id = _store.ListMessages(_inbox, 10).Single().Id;
        _store.SaveBody(id, [1, 2, 3]);

        // Server renumbered the folder: same message, new UIDVALIDITY and UID.
        _store.StoreServerMessages(_inbox, 2, [Server(900, "Hallo", "abc@example.org")]);

        var row = Assert.Single(_store.GetSyncState(_inbox));
        Assert.Equal(id, row.Id);
        Assert.Equal(900u, row.Uid);
        Assert.Equal([1, 2, 3], _store.GetBody(id));
    }

    [Fact]
    public void Folder_missing_from_one_listing_is_hidden_and_only_removed_after_the_second()
    {
        _store.StoreServerMessages(_trash, 1, [Server(1, "x")]);

        _store.ApplyFolderListing(_account, new HashSet<long> { _inbox });
        Assert.NotNull(_store.GetFolder(_trash));
        Assert.DoesNotContain(_store.GetFolders(_account), f => f.Id == _trash);

        _store.ApplyFolderListing(_account, new HashSet<long> { _inbox });
        Assert.Null(_store.GetFolder(_trash));
    }

    [Fact]
    public void Undo_send_wins_only_before_sending_starts()
    {
        var summary = new OutboxSummary("bob@example.org", "Hallo", "1@example.org");
        var a = _store.AddToOutbox(_account, [1], summary, DateTimeOffset.UtcNow.AddSeconds(5));
        var b = _store.AddToOutbox(_account, [2], summary, DateTimeOffset.UtcNow.AddSeconds(5));

        // a: taken back first → the sender can no longer claim it
        Assert.True(_store.TryCancelQueued(a));
        Assert.False(_store.TryBeginSending(a, 1));

        // b: sender claimed it first → too late to take it back
        Assert.True(_store.TryBeginSending(b, 1));
        Assert.False(_store.TryCancelQueued(b));
        Assert.Equal(OutboxState.Sending, _store.GetOutbox().Single(o => o.Id == b).State);
    }

    [Fact]
    public void Delayed_mail_waits_in_the_outbox_until_its_time()
    {
        var id = _store.AddToOutbox(_account, [1], new OutboxSummary("bob@example.org", "Hallo", "1@example.org"),
            DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.True(_store.GetOutbox().Single(o => o.Id == id).NextAttempt > DateTimeOffset.UtcNow.AddSeconds(20));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        using var crypto = new Crypto(RandomNumberGenerator.GetBytes(32));
        var blob = crypto.EncryptString("geheim", "message.body");
        blob[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => crypto.DecryptString(blob, "message.body"));
    }

    [Fact]
    public void Ciphertext_cannot_be_moved_to_another_column()
    {
        using var crypto = new Crypto(RandomNumberGenerator.GetBytes(32));
        var blob = crypto.EncryptString("geheim", "account.secret");

        Assert.ThrowsAny<CryptographicException>(() => crypto.DecryptString(blob, "message.envelope"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
