using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Wisp.Core.Mail;
using Wisp.Core.Storage;
using Envelope = Wisp.Core.Storage.Envelope;

namespace Wisp.Core.Sync;

public sealed partial class AccountWorker
{
    const int FetchBatch = 50;
    const int PrefetchPerRound = 20;

    static readonly MessageSummaryItems HeaderItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure |
        MessageSummaryItems.PreviewText;

    async Task SyncFolderListAsync(ImapClient imap, CancellationToken ct)
    {
        var ns = imap.PersonalNamespaces.Count > 0 ? imap.PersonalNamespaces[0] : new FolderNamespace('/', "");
        // GetFolders either returns the complete list or throws: a partial list can never reach ApplyFolderListing.
        var folders = (await imap.GetFoldersAsync(ns, StatusItems.None, false, ct)).ToList();
        if (!folders.Any(f => f.FullName.Equals(imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
            folders.Insert(0, imap.Inbox);

        var present = new HashSet<long>();
        foreach (var folder in folders)
        {
            if (folder.Attributes.HasFlag(FolderAttributes.NonExistent) || folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                continue;
            var role = ImapMapping.RoleOf(folder);
            var depth = folder.DirectorySeparator == '\0' ? 0 : folder.FullName.Count(c => c == folder.DirectorySeparator);
            var syncByDefault = role is not (FolderRole.All or FolderRole.Flagged);
            present.Add(_store.UpsertFolder(_accountId, folder.FullName, folder.Name, depth, role, syncByDefault));
        }
        _store.ApplyFolderListing(_accountId, present);
        Raise(ChangeKind.Folders);
    }

    async Task SyncAllFoldersAsync(ImapClient imap, CancellationToken ct)
    {
        foreach (var folder in _store.GetFolders(_accountId).Where(f => f.SyncEnabled))
        {
            try
            {
                await SyncFolderAsync(imap, folder, ct);
            }
            catch (Exception ex) when (MailConnector.Classify(ex) == FailureKind.Rejected)
            {
                // The server refuses this one folder (e.g. deleted meanwhile). Others still sync.
            }
            await ServeBodyRequestsAsync(imap, ct); // keep "open message" responsive during long syncs
        }
    }

    /// <summary>
    /// Brings one folder in line with the server. Steps that can lose data (absence detection) only run
    /// after every earlier step completed — any exception aborts the folder before it gets there.
    /// </summary>
    async Task SyncFolderAsync(ImapClient imap, FolderRow row, CancellationToken ct)
    {
        var folder = await imap.GetFolderAsync(row.Path, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uidValidity = folder.UidValidity;

        var modSeq = row.HighestModSeq;
        if (row.UidValidity != 0 && row.UidValidity != uidValidity)
            modSeq = 0; // the server renumbered the folder; local rows get re-attached by Message-ID below

        // 1. Complete UID listing. Throws on any problem.
        var serverUids = await folder.SearchAsync(SearchQuery.All, ct);
        var serverSet = serverUids.Select(u => u.Id).ToHashSet();

        // 2. Headers of messages we do not have yet, newest first, limited to the configured window.
        var local = _store.GetSyncState(row.Id);
        var localUids = local.Where(m => m.UidValidity == uidValidity && m.Uid > 0).Select(m => m.Uid).ToHashSet();
        var window = serverUids.OrderByDescending(u => u.Id).Take(Math.Max(100, _settings().Sync.HeadersPerFolder)).ToList();
        var toFetch = window.Where(u => !localUids.Contains(u.Id)).ToList();
        var changed = false;

        foreach (var batch in toFetch.Chunk(FetchBatch))
        {
            var summaries = await folder.FetchAsync(batch, HeaderItems, ct);
            var added = _store.StoreServerMessages(row.Id, uidValidity, summaries.Select(ToServerMessage));
            changed = true;
            if (added > 0)
            {
                _store.RecountFolder(row.Id);
                Raise(ChangeKind.Messages, row.Id); // show progress while a large folder fills
            }
        }

        // 3. Flag changes of messages we already have.
        if (localUids.Count > 0)
        {
            var known = new UniqueIdSet(localUids.Where(serverSet.Contains).Select(u => new UniqueId(uidValidity, u)), SortOrder.Ascending);
            if (known.Count > 0)
            {
                var useModSeq = modSeq > 0 && folder.Supports(FolderFeature.ModSequences);
                var updates = useModSeq
                    ? await folder.FetchAsync(known, modSeq, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.ModSeq, ct)
                    : await folder.FetchAsync(known, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);
                if (updates.Count > 0)
                {
                    _store.UpdateServerFlags(row.Id, uidValidity, updates.Select(s => (s.UniqueId.Id, ImapMapping.ToLocal(s.Flags))));
                    changed = true;
                }
            }
        }

        // 4. Absence detection against the complete listing from step 1 (two-phase, mass-delete guard).
        var plan = Reconciler.PlanAbsence(_store.GetSyncState(row.Id), uidValidity, serverSet, row.MassDeleteApproved);
        if (!plan.IsEmpty)
        {
            _store.ApplyAbsencePlan(plan);
            changed = true;
        }
        if (plan.MassDeleteGuardTriggered != row.MassDeleteWarning || row.MassDeleteApproved)
        {
            _store.SetMassDeleteWarning(row.Id, plan.MassDeleteGuardTriggered);
            if (plan.MassDeleteGuardTriggered)
                Raise(ChangeKind.Notice, row.Id, $"Im Ordner „{row.Name}“ fehlen auf dem Server plötzlich sehr viele Mails. Wisp hat lokal nichts entfernt.");
            Raise(ChangeKind.Folders);
        }

        var highest = serverUids.Count > 0 ? serverUids.Max(u => u.Id) : 0;
        var newModSeq = folder.Supports(FolderFeature.ModSequences) ? folder.HighestModSeq : 0UL;
        _store.UpdateFolderSyncState(row.Id, uidValidity, highest, 0, newModSeq);
        _store.RecountFolder(row.Id);
        if (changed) Raise(ChangeKind.Messages, row.Id);
        Raise(ChangeKind.Folders, row.Id);
    }

    static ServerMessage ToServerMessage(IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From.Mailboxes.FirstOrDefault();
        var preview = (s.PreviewText ?? "").ReplaceLineEndings(" ").Trim();
        if (preview.Length > 200) preview = preview[..200];
        return new ServerMessage(
            s.UniqueId.Id,
            s.InternalDate ?? env?.Date ?? DateTimeOffset.UtcNow,
            (long)(s.Size ?? 0),
            ImapMapping.ToLocal(s.Flags),
            new Envelope
            {
                Subject = env?.Subject ?? "",
                FromName = from?.Name ?? "",
                FromAddress = from?.Address ?? "",
                To = Truncate(env?.To.ToString() ?? "", 500),
                Cc = env?.Cc.Count > 0 ? Truncate(env.Cc.ToString(), 500) : null,
                MessageId = env?.MessageId,
                Preview = preview,
            },
            s.Attachments?.Any() == true);
    }

    static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + " …";

    async Task ServeBodyRequestsAsync(ImapClient imap, CancellationToken ct)
    {
        while (_bodyRequests.TryDequeue(out var request))
        {
            try
            {
                request.Result.TrySetResult(await DownloadBodyAsync(imap, request.MessageId, ct));
            }
            catch (Exception ex) when (MailConnector.Classify(ex) == FailureKind.Rejected)
            {
                request.Result.TrySetResult(null);
            }
            catch
            {
                request.Result.TrySetResult(null);
                throw;
            }
        }
    }

    async Task<byte[]?> DownloadBodyAsync(ImapClient imap, long messageId, CancellationToken ct)
    {
        if (_store.GetBody(messageId) is { } cached) return cached;
        var msg = _store.GetMessageRef(messageId);
        if (msg is null) return null;

        // A message with a pending move still lives in its old folder on the server.
        var (path, uid, validity) = msg.SrcFolderId is long src && _store.GetFolder(src) is { } srcFolder
            ? (srcFolder.Path, msg.SrcUid ?? 0, srcFolder.UidValidity)
            : (msg.FolderPath, msg.Uid, msg.UidValidity);
        if (uid == 0) return null;

        var folder = await imap.GetFolderAsync(path, ct);
        if (!folder.IsOpen) await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        if (folder.UidValidity != validity) return null;

        // BODY.PEEK: reading here never marks the message as read on the server.
        using var stream = await folder.GetStreamAsync(new UniqueId(validity, uid), ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var raw = buffer.ToArray();
        _store.SaveBody(messageId, raw);
        return raw;
    }

    /// <summary>Downloads recent messages in small portions so they can be read offline.</summary>
    async Task PrefetchAsync(ImapClient imap, CancellationToken ct)
    {
        var sync = _settings().Sync;
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, sync.OfflineDays));
        var budget = PrefetchPerRound;
        _morePrefetch = false;
        if (sync.OfflineDays <= 0) return;

        foreach (var row in _store.GetFolders(_accountId).Where(f => f.SyncEnabled && f.Role is not (FolderRole.Trash or FolderRole.Junk)))
        {
            var candidates = _store.GetPrefetchCandidates(row.Id, row.UidValidity, since, sync.PrefetchMaxKb * 1024L, budget + 1);
            if (candidates.Count == 0) continue;

            var folder = await imap.GetFolderAsync(row.Path, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            if (folder.UidValidity != row.UidValidity) continue;

            var portion = candidates.Take(budget).ToList();
            foreach (var (id, uid) in portion)
            {
                try
                {
                    using var stream = await folder.GetStreamAsync(new UniqueId(row.UidValidity, uid), ct);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);
                    _store.SaveBody(id, buffer.ToArray());
                }
                catch (Exception ex) when (MailConnector.Classify(ex) == FailureKind.Rejected)
                {
                    // Gone meanwhile; the next folder sync handles it.
                }
                if (!_bodyRequests.IsEmpty)
                {
                    await ServeBodyRequestsAsync(imap, ct);
                    if (!folder.IsOpen) await folder.OpenAsync(FolderAccess.ReadOnly, ct); // serving may select another folder
                }
            }
            budget -= portion.Count;
            if (candidates.Count > portion.Count || budget <= 0)
            {
                _morePrefetch = true;
                return;
            }
        }
    }
}
