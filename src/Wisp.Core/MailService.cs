using Wisp.Core.Accounts;
using Wisp.Core.Mail;
using Wisp.Core.Rendering;
using Wisp.Core.Security;
using Wisp.Core.Settings;
using Wisp.Core.Storage;
using Wisp.Core.Sync;

namespace Wisp.Core;

public sealed record AccountStatus(ConnectionState State, string? Text, bool IsSyncing, int PendingChanges, int Outbox);

public enum DeleteOutcome { MovedToTrash, NeedsConfirmation, Nothing }

public sealed record OpenResult(RenderedMail? Mail, string? Problem);

/// <summary>
/// The only entry point for the UI. Reads come from the local store (instant, work offline);
/// writes are queued and executed by the account workers in the background.
/// </summary>
public sealed class MailService : IAsyncDisposable
{
    readonly MailStore _store;
    readonly Crypto _crypto;
    readonly Connectivity _net = new();
    readonly Dictionary<long, AccountWorker> _workers = [];

    MailService(MailStore store, Crypto crypto, AppSettings settings)
    {
        _store = store;
        _crypto = crypto;
        Settings = settings;
    }

    public AppSettings Settings { get; }

    /// <summary>Raised from background threads. UI code must dispatch to its own thread.</summary>
    public event Action<WorkerChange>? Changed;

    public static MailService Open(byte[] dataKey, AppSettings settings)
    {
        var crypto = new Crypto(dataKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
        return new MailService(new MailStore(AppPaths.DatabaseFile, crypto), crypto, settings);
    }

    public void SaveSettings() => Settings.Save(AppPaths.SettingsFile);

    public void Start()
    {
        _store.MarkInterruptedSendsUncertain();
        if (Settings.Sync.OfflineDays > 0)
            _store.EvictBodies(DateTimeOffset.UtcNow.AddDays(-Math.Max(Settings.Sync.OfflineDays, 7) * 4));
        foreach (var account in _store.GetAccounts()) StartWorker(account.Id);
    }

    void StartWorker(long accountId)
    {
        var worker = new AccountWorker(accountId, _store, () => Settings, _net);
        worker.Changed += change => Changed?.Invoke(change);
        _workers[accountId] = worker;
        worker.Start();
    }

    AccountWorker? Worker(long accountId) => _workers.GetValueOrDefault(accountId);

    // ───────────────────────── Accounts ─────────────────────────

    public IReadOnlyList<AccountRow> GetAccounts() => _store.GetAccounts();

    /// <summary>Tests the login on both servers, then stores the account (encrypted) and starts syncing.</summary>
    public async Task<long> AddAccountAsync(AccountConfig config, Credentials credentials, CancellationToken ct)
    {
        if (_store.GetAccounts().Any(a => a.Config.Email.Equals(config.Email, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Dieses Konto ist bereits eingerichtet.");
        await MailConnector.TestAsync(config, credentials, ct);
        var id = _store.AddAccount(config, credentials);
        StartWorker(id);
        Changed?.Invoke(new WorkerChange(id, ChangeKind.Folders));
        return id;
    }

    /// <summary>Removes the account from this device only. Mail on the server is not touched.</summary>
    public async Task RemoveAccountAsync(long accountId)
    {
        if (_workers.Remove(accountId, out var worker)) await worker.DisposeAsync();
        _store.DeleteAccount(accountId);
        Changed?.Invoke(new WorkerChange(accountId, ChangeKind.Folders));
    }

    public async Task UpdatePasswordAsync(long accountId, string password, CancellationToken ct)
    {
        var account = _store.GetAccount(accountId) ?? throw new InvalidOperationException("Unbekanntes Konto.");
        var credentials = Credentials.FromPassword(password);
        await MailConnector.TestAsync(account.Config, credentials, ct);
        _store.UpdateCredentials(accountId, credentials);
        Worker(accountId)?.RequestFullSync();
    }

    public async Task ReauthorizeAsync(long accountId, CancellationToken ct)
    {
        var account = _store.GetAccount(accountId) ?? throw new InvalidOperationException("Unbekanntes Konto.");
        var provider = OAuthConfig.For(account.Config.Auth) ?? throw new InvalidOperationException("Konto nutzt keine Browser-Anmeldung.");
        _store.UpdateCredentials(accountId, await OAuthFlow.AuthorizeAsync(provider, account.Config.Email, ct));
        Worker(accountId)?.RequestFullSync();
    }

    public void RenameAccount(long accountId, string displayName)
    {
        if (_store.GetAccount(accountId) is { } account)
            _store.UpdateAccount(accountId, account.Config with { DisplayName = displayName });
    }

    public AccountStatus GetStatus(long accountId)
    {
        var worker = Worker(accountId);
        return new AccountStatus(worker?.State ?? ConnectionState.Offline, worker?.StatusText, worker?.IsSyncing ?? false,
            _store.CountPendingOps(accountId), _store.GetOutbox(accountId).Count);
    }

    public void Refresh()
    {
        foreach (var worker in _workers.Values) worker.RequestFullSync();
    }

    // ───────────────────────── Folders & messages ─────────────────────────

    public IReadOnlyList<FolderRow> GetFolders(long accountId) => _store.GetFolders(accountId);

    public FolderRow? GetFolder(long folderId) => _store.GetFolder(folderId);

    public void FolderOpened(long folderId)
    {
        if (_store.GetFolder(folderId) is { } folder) Worker(folder.AccountId)?.RequestFolder(folderId);
    }

    public void ApproveMassDelete(long folderId)
    {
        if (_store.GetFolder(folderId) is not { } folder) return;
        _store.ApproveMassDelete(folderId);
        Worker(folder.AccountId)?.RequestFolder(folderId);
    }

    public IReadOnlyList<MessageListItem> ListMessages(long folderId, string? search, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(search)) return _store.ListMessages(folderId, limit);
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _store.ListMessages(folderId, 20_000)
            .Where(m => terms.All(t =>
                m.Envelope.Subject.Contains(t, StringComparison.CurrentCultureIgnoreCase) ||
                m.Envelope.FromName.Contains(t, StringComparison.CurrentCultureIgnoreCase) ||
                m.Envelope.FromAddress.Contains(t, StringComparison.CurrentCultureIgnoreCase) ||
                m.Envelope.To.Contains(t, StringComparison.CurrentCultureIgnoreCase) ||
                m.Envelope.Preview.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .Take(limit)
            .ToList();
    }

    public MessageListItem? GetMessage(long messageId) => _store.GetMessage(messageId);

    /// <summary>Loads a message from the offline cache or, if online, from the server. Marks it as read.</summary>
    public async Task<OpenResult> OpenAsync(long messageId, bool? showHtml, bool? allowRemoteImages, CancellationToken ct)
    {
        var item = _store.GetMessage(messageId);
        if (item is null) return new OpenResult(null, "Diese Mail existiert nicht mehr.");

        var raw = _store.GetBody(messageId) ?? await (Worker(item.AccountId)?.FetchBodyAsync(messageId, ct) ?? Task.FromResult<byte[]?>(null));
        if (raw is null)
        {
            var offline = Worker(item.AccountId)?.State is not (ConnectionState.Online or ConnectionState.Degraded);
            return new OpenResult(null, offline
                ? "Diese Mail ist noch nicht offline verfügbar. Sie wird geladen, sobald eine Verbindung besteht."
                : "Die Mail konnte gerade nicht geladen werden. Bitte später erneut versuchen.");
        }

        if (!item.IsSeen) SetSeen([messageId], true);
        var options = new RenderOptions(showHtml ?? Settings.Privacy.ShowHtml, allowRemoteImages ?? Settings.Privacy.LoadRemoteImages);
        return new OpenResult(await Task.Run(() => MailRenderer.Render(raw, options), ct), null);
    }

    public void SetSeen(IEnumerable<long> ids, bool seen) => Apply(_store.SetFlagLocally(ids, MessageFlags.Seen, seen));

    public void SetFlagged(IEnumerable<long> ids, bool flagged) => Apply(_store.SetFlagLocally(ids, MessageFlags.Flagged, flagged));

    public void Move(IReadOnlyCollection<long> ids, long targetFolderId) => Apply(_store.MoveLocally(ids, targetFolderId));

    /// <summary>
    /// "Delete" moves to the trash. Inside the trash (or without one) the caller has to confirm a permanent deletion.
    /// </summary>
    public DeleteOutcome Delete(IReadOnlyCollection<long> ids)
    {
        var first = ids.Select(_store.GetMessage).FirstOrDefault(m => m is not null);
        if (first is null) return DeleteOutcome.Nothing;
        var trash = _store.FindFolderByRole(first.AccountId, FolderRole.Trash);
        if (trash is null || first.FolderId == trash.Id) return DeleteOutcome.NeedsConfirmation;
        Move(ids, trash.Id);
        return DeleteOutcome.MovedToTrash;
    }

    public void DeletePermanently(IReadOnlyCollection<long> ids) => Apply(_store.ExpungeLocally(ids));

    public FolderRow? ArchiveFolder(long accountId) =>
        _store.FindFolderByRole(accountId, FolderRole.Archive) ?? _store.FindFolderByRole(accountId, FolderRole.All);

    public bool Archive(IReadOnlyCollection<long> ids)
    {
        var first = ids.Select(_store.GetMessage).FirstOrDefault(m => m is not null);
        if (first is null || ArchiveFolder(first.AccountId) is not { } archive) return false;
        Move(ids, archive.Id);
        return true;
    }

    void Apply(IReadOnlySet<long> touchedFolders)
    {
        foreach (var accountId in touchedFolders.Select(f => _store.GetFolder(f)?.AccountId).OfType<long>().Distinct())
        {
            Changed?.Invoke(new WorkerChange(accountId, ChangeKind.Messages));
            Changed?.Invoke(new WorkerChange(accountId, ChangeKind.Folders));
            Worker(accountId)?.Wake();
        }
    }

    public async Task SaveAttachmentAsync(long messageId, int index, Stream target, CancellationToken ct)
    {
        var raw = _store.GetBody(messageId) ?? throw new InvalidOperationException("Die Mail ist nicht geladen.");
        var attachment = MailRenderer.Parse(raw).Attachments.ElementAt(index);
        switch (attachment)
        {
            case MimeKit.MimePart part:
                await part.Content!.DecodeToAsync(target, ct);
                break;
            case MimeKit.MessagePart message:
                await message.Message!.WriteToAsync(target, ct);
                break;
        }
    }

    // ───────────────────────── Sending ─────────────────────────

    /// <summary>Puts the message into the encrypted outbox. Returns an error text, or null on success.</summary>
    public string? Send(Draft draft)
    {
        if (Composer.Validate(draft) is { } problem) return problem;
        var account = _store.GetAccount(draft.AccountId) ?? throw new InvalidOperationException("Unbekanntes Konto.");

        MimeKit.MimeMessage? forwardSource = null;
        if (draft.ForwardsAttachmentsOf is long source)
        {
            var raw = _store.GetBody(source);
            if (raw is null) return "Die weitergeleitete Mail ist nicht offline verfügbar; ihre Anhänge können nicht mitgeschickt werden.";
            forwardSource = MailRenderer.Parse(raw);
        }

        var message = Composer.Build(draft, account.Config, forwardSource);
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        _store.AddToOutbox(draft.AccountId, buffer.ToArray(),
            new OutboxSummary(string.Join(", ", message.To.Mailboxes.Select(m => m.Address)), message.Subject ?? "", message.MessageId!));
        if (draft.RepliesTo is long replied) _store.SetFlagLocally([replied], MessageFlags.Answered, true);

        Changed?.Invoke(new WorkerChange(draft.AccountId, ChangeKind.Outbox));
        Worker(draft.AccountId)?.Wake();
        return null;
    }

    public IReadOnlyList<OutboxItem> GetOutbox() => _store.GetOutbox();

    /// <summary>User decision for a failed or uncertain message: send it (again).</summary>
    public void RetrySend(long outboxId)
    {
        var item = _store.GetOutbox().FirstOrDefault(o => o.Id == outboxId);
        if (item is null) return;
        _store.UpdateOutbox(outboxId, OutboxState.Queued, 0, DateTimeOffset.UtcNow, null);
        Changed?.Invoke(new WorkerChange(item.AccountId, ChangeKind.Outbox));
        Worker(item.AccountId)?.Wake();
    }

    public void DiscardFromOutbox(long outboxId)
    {
        var item = _store.GetOutbox().FirstOrDefault(o => o.Id == outboxId);
        if (item is null || item.State == OutboxState.Sending) return;
        _store.DeleteFromOutbox(outboxId);
        Changed?.Invoke(new WorkerChange(item.AccountId, ChangeKind.Outbox));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values) await worker.DisposeAsync();
        _workers.Clear();
        _net.Dispose();
        _store.Dispose();
        _crypto.Dispose();
    }
}
