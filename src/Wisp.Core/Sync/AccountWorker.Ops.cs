using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Wisp.Core.Mail;
using Wisp.Core.Storage;
using KitFlags = MailKit.MessageFlags;

namespace Wisp.Core.Sync;

public sealed partial class AccountWorker
{
    static readonly TimeSpan WaitForUid = TimeSpan.FromSeconds(30);

    // ───────────────────────── Queued operations ─────────────────────────

    async Task ReplayOperationsAsync(ImapClient imap, CancellationToken ct)
    {
        var ops = _store.GetDueOps(_accountId);
        if (ops.Count == 0) return;

        var touched = false;
        foreach (var op in ops)
        {
            try
            {
                await ExecuteAsync(imap, op, ct);
                touched = true;
            }
            catch (Exception ex) when (MailConnector.Classify(ex) == FailureKind.Rejected)
            {
                // The server refuses this for good: undo it locally so the view matches the server again.
                Undo(op);
                _store.CompleteOp(op.Id);
                touched = true;
                Raise(ChangeKind.Notice, null, "Eine Aktion konnte auf dem Server nicht ausgeführt werden und wurde rückgängig gemacht. " +
                                               MailConnector.Describe(ex));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _store.RetryOpLater(op.Id, Backoff.For(op.Attempts));
                throw; // most likely the connection is gone; the outer loop reconnects
            }
        }
        if (touched)
        {
            Raise(ChangeKind.Messages);
            Raise(ChangeKind.Folders);
        }
    }

    async Task ExecuteAsync(ImapClient imap, PendingOp op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OpKind.AddFlags or OpKind.RemoveFlags:
                await ExecuteFlagsAsync(imap, op, ct);
                break;
            case OpKind.Move:
                await ExecuteMoveAsync(imap, op, ct);
                break;
            case OpKind.Expunge:
                await ExecuteExpungeAsync(imap, op, ct);
                break;
        }
    }

    async Task ExecuteFlagsAsync(ImapClient imap, PendingOp op, CancellationToken ct)
    {
        var msg = op.MessageId is long id ? _store.GetMessageRef(id) : null;
        if (msg is null)
        {
            _store.CompleteOp(op.Id); // message no longer cached: nothing left to sync
            return;
        }
        // A message whose move is still queued lives in its old folder on the server: set the flag there,
        // it travels along with the move. (Waiting for the move would deadlock: the move is queued
        // behind this operation.)
        var (path, uid) = msg.SrcFolderId is long src && _store.GetFolder(src) is { } srcFolder
            ? (srcFolder.Path, msg.SrcUid ?? 0)
            : (msg.FolderPath, msg.Uid);
        if (uid == 0)
        {
            _store.RetryOpLater(op.Id, WaitForUid); // moved without UIDPLUS: the next sync of the target finds its UID
            return;
        }

        var folder = await imap.GetFolderAsync(path, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (folder.UidValidity == msg.UidValidity)
        {
            var request = new StoreFlagsRequest(op.Kind == OpKind.AddFlags ? StoreAction.Add : StoreAction.Remove,
                ImapMapping.ToServer(op.Payload.Flags)) { Silent = true };
            await folder.StoreAsync(new UniqueId(msg.UidValidity, uid), request, ct);
        }
        // UIDVALIDITY changed: the UID is meaningless now. The next sync re-reads the server's flags.
        _store.CompleteOp(op.Id);
    }

    async Task ExecuteMoveAsync(ImapClient imap, PendingOp op, CancellationToken ct)
    {
        var p = op.Payload;
        var source = await imap.GetFolderAsync(p.FolderPath!, ct);
        var target = await imap.GetFolderAsync(p.TargetPath!, ct);
        await source.OpenAsync(FolderAccess.ReadWrite, ct);

        var uid = await LocateAsync(source, p, ct);
        if (uid is UniqueId found)
        {
            var moved = await source.MoveToAsync(found, target, ct);
            var validity = moved?.Validity is > 0 ? moved.Value.Validity : await UidValidityOf(target, ct);
            if (op.MessageId is long id) _store.MarkMoveDone(id, validity, moved?.Id);
            _store.CompleteOp(op.Id);
            return;
        }

        // Not in the source folder (any more). Maybe a previous attempt already moved it.
        await target.OpenAsync(FolderAccess.ReadOnly, ct);
        var inTarget = await LocateByMessageIdAsync(target, p.MessageIdHeader, ct);
        if (op.MessageId is long messageId)
        {
            if (inTarget is UniqueId arrived) _store.MarkMoveDone(messageId, target.UidValidity, arrived.Id);
            else _store.RevertMove(messageId, p, sourceMissing: true); // deleted elsewhere; normal two-phase removal follows
        }
        _store.CompleteOp(op.Id);
    }

    async Task ExecuteExpungeAsync(ImapClient imap, PendingOp op, CancellationToken ct)
    {
        var p = op.Payload;
        var folder = await imap.GetFolderAsync(p.FolderPath!, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);

        if (await LocateAsync(folder, p, ct) is UniqueId uid)
        {
            await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
            if (imap.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                await folder.ExpungeAsync([uid], ct);
            else
                await folder.ExpungeAsync(ct);
        }
        // Either we just expunged it, or the server confirms it is not there.
        if (op.MessageId is long id) _store.DeleteMessageConfirmed(id);
        _store.CompleteOp(op.Id);
    }

    /// <summary>Finds the message in an opened folder: by UID if still valid, otherwise by Message-ID.</summary>
    static async Task<UniqueId?> LocateAsync(IMailFolder folder, OpPayload p, CancellationToken ct)
    {
        if (p.Uid > 0 && folder.UidValidity == p.UidValidity)
        {
            var hit = await folder.SearchAsync(SearchQuery.Uids([new UniqueId(p.UidValidity, p.Uid)]), ct);
            return hit.Count > 0 ? hit[0] : null;
        }
        return await LocateByMessageIdAsync(folder, p.MessageIdHeader, ct);
    }

    static async Task<UniqueId?> LocateByMessageIdAsync(IMailFolder folder, string? messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return null;
        var hits = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), ct);
        return hits.Count > 0 ? hits[^1] : null;
    }

    static async Task<uint> UidValidityOf(IMailFolder folder, CancellationToken ct)
    {
        if (!folder.IsOpen) await folder.StatusAsync(StatusItems.UidValidity, ct);
        return folder.UidValidity;
    }

    void Undo(PendingOp op)
    {
        if (op.MessageId is not long id) return;
        switch (op.Kind)
        {
            case OpKind.Move: _store.RevertMove(id, op.Payload, sourceMissing: false); break;
            case OpKind.Expunge: _store.CancelDeletion(id); break;
            // Flags: dropping the op is enough, the next sync restores the server's flags.
        }
    }

    // ───────────────────────── Outbox ─────────────────────────

    async Task ProcessOutboxAsync(ImapClient imap, CancellationToken ct)
    {
        var items = _store.GetOutbox(_accountId);
        if (items.Count == 0) return;
        var now = DateTimeOffset.UtcNow;

        foreach (var item in items)
        {
            switch (item.State)
            {
                case OutboxState.Queued when item.NextAttempt <= now:
                    await SendAsync(item, ct);
                    break;
                case OutboxState.Accepted:
                    await FileIntoSentAsync(imap, item, ct);
                    break;
                case OutboxState.Uncertain when item.Attempts == 0:
                    // Check once whether the interrupted send actually went through.
                    if (await IsInSentAsync(imap, item.Summary.MessageId, ct)) _store.DeleteFromOutbox(item.Id);
                    else _store.UpdateOutbox(item.Id, OutboxState.Uncertain, 1, now,
                        "Der Versand wurde unterbrochen. Möglicherweise wurde die Mail bereits gesendet.");
                    Raise(ChangeKind.Outbox);
                    break;
            }
        }
    }

    async Task SendAsync(OutboxItem item, CancellationToken ct)
    {
        var account = Account;
        MailKit.Net.Smtp.SmtpClient smtp;
        try
        {
            smtp = await MailConnector.ConnectSmtpAsync(account, GetCredentialsAsync, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Nothing was transmitted yet, so retrying is always safe.
            var kind = MailConnector.Classify(ex);
            _store.UpdateOutbox(item.Id, kind == FailureKind.Rejected ? OutboxState.Failed : OutboxState.Queued,
                item.Attempts + 1, DateTimeOffset.UtcNow + Backoff.For(item.Attempts), MailConnector.Describe(ex));
            Raise(ChangeKind.Outbox);
            return;
        }

        using (smtp)
        {
            // Claim it atomically: if the user pressed "Rückgängig" meanwhile, it is gone and nothing is sent.
            if (!_store.TryBeginSending(item.Id, item.Attempts + 1)) return;
            var message = MimeMessage.Load(new MemoryStream(_store.GetOutboxRaw(item.Id)));
            Raise(ChangeKind.Outbox);
            try
            {
                await smtp.SendAsync(message, ct);
            }
            catch (MailKit.Net.Smtp.SmtpCommandException ex)
            {
                // The server answered with an error, so it definitely did not take the message.
                var permanent = (int)ex.StatusCode >= 500;
                _store.UpdateOutbox(item.Id, permanent ? OutboxState.Failed : OutboxState.Queued,
                    item.Attempts + 1, DateTimeOffset.UtcNow + Backoff.For(item.Attempts), MailConnector.Describe(ex));
                Raise(ChangeKind.Outbox);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Connection broke mid-transfer: the server may or may not have accepted it. Never resend blindly.
                _store.UpdateOutbox(item.Id, OutboxState.Uncertain, 0, DateTimeOffset.UtcNow, MailConnector.Describe(ex));
                Raise(ChangeKind.Outbox);
                return;
            }
            _store.UpdateOutbox(item.Id, OutboxState.Accepted, item.Attempts + 1, DateTimeOffset.UtcNow, null);
            Raise(ChangeKind.Outbox);
            try { await smtp.DisconnectAsync(true, ct); } catch { /* already delivered */ }
        }
    }

    async Task FileIntoSentAsync(ImapClient imap, OutboxItem item, CancellationToken ct)
    {
        var sent = _store.FindFolderByRole(_accountId, FolderRole.Sent);
        if (!Account.ServerSavesSent && sent is not null && !await IsInSentAsync(imap, item.Summary.MessageId, ct))
        {
            var folder = await imap.GetFolderAsync(sent.Path, ct);
            var message = MimeMessage.Load(new MemoryStream(_store.GetOutboxRaw(item.Id)));
            await folder.AppendAsync(new AppendRequest(message, KitFlags.Seen), ct);
        }
        _store.DeleteFromOutbox(item.Id);
        if (sent is not null) RequestFolder(sent.Id);
        Raise(ChangeKind.Outbox);
    }

    async Task<bool> IsInSentAsync(ImapClient imap, string messageId, CancellationToken ct)
    {
        if (_store.FindFolderByRole(_accountId, FolderRole.Sent) is not { } sent) return false;
        var folder = await imap.GetFolderAsync(sent.Path, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        return await LocateByMessageIdAsync(folder, messageId, ct) is not null;
    }
}
