using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using Wisp.Core.Accounts;
using Wisp.Core.Mail;
using Wisp.Core.Settings;
using Wisp.Core.Storage;
using MailStore = Wisp.Core.Storage.MailStore;

namespace Wisp.Core.Sync;

public enum ChangeKind { Status, Folders, Messages, Outbox, Notice }

public sealed record WorkerChange(long AccountId, ChangeKind Kind, long? FolderId = null, string? Text = null);

/// <summary>
/// One background loop per account, owning the account's single IMAP connection.
/// Order per round: requested bodies → queued operations → outbox → folder sync → offline prefetch → IDLE.
/// Any connection failure ends the round; nothing is lost because all state lives in <see cref="MailStore"/>.
/// </summary>
public sealed partial class AccountWorker : IAsyncDisposable
{
    static readonly TimeSpan IdleRenewal = TimeSpan.FromMinutes(9);

    readonly long _accountId;
    readonly MailStore _store;
    readonly Func<AppSettings> _settings;
    readonly Connectivity _net;
    readonly AsyncSignal _wake = new();
    readonly ConcurrentQueue<BodyRequest> _bodyRequests = new();
    readonly ConcurrentQueue<long> _folderRequests = new();
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    bool _inboxDirty = true;
    bool _morePrefetch;
    DateTimeOffset _lastFullSync = DateTimeOffset.MinValue;

    sealed record BodyRequest(long MessageId, TaskCompletionSource<byte[]?> Result);

    public AccountWorker(long accountId, MailStore store, Func<AppSettings> settings, Connectivity net)
    {
        _accountId = accountId;
        _store = store;
        _settings = settings;
        _net = net;
        _net.Changed += Wake;
    }

    public long AccountId => _accountId;
    public ConnectionState State { get; private set; } = ConnectionState.Offline;
    public string? StatusText { get; private set; }
    public bool IsSyncing { get; private set; }

    public event Action<WorkerChange>? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Wake() => _wake.Set();

    /// <summary>Forces a full sync on the next round (e.g. "refresh" button).</summary>
    public void RequestFullSync()
    {
        _lastFullSync = DateTimeOffset.MinValue;
        Wake();
    }

    /// <summary>Syncs one folder soon, even if it is not synced automatically.</summary>
    public void RequestFolder(long folderId)
    {
        _folderRequests.Enqueue(folderId);
        Wake();
    }

    /// <summary>Downloads a message that is not cached yet. Returns null when offline or if it no longer exists.</summary>
    public async Task<byte[]?> FetchBodyAsync(long messageId, CancellationToken ct)
    {
        if (State is not (ConnectionState.Online or ConnectionState.Degraded or ConnectionState.Connecting)) return null;
        var request = new BodyRequest(messageId, new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _bodyRequests.Enqueue(request);
        Wake();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { return await request.Result.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
    }

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = new Backoff();
        while (!ct.IsCancellationRequested)
        {
            if (!_net.NetworkAvailable)
            {
                SetState(ConnectionState.Offline, "Offline – Änderungen werden später übertragen.");
                FailBodyRequests();
                await WaitQuietly(TimeSpan.FromMinutes(5), ct);
                continue;
            }

            ImapClient? imap = null;
            try
            {
                SetState(ConnectionState.Connecting, "Verbinde …");
                var account = Account;
                imap = await MailConnector.ConnectImapAsync(account, GetCredentialsAsync, ct);
                backoff.Reset();
                SetState(ConnectionState.Online, null);
                await SessionAsync(imap, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FailBodyRequests();
                IsSyncing = false;
                switch (MailConnector.Classify(ex))
                {
                    case FailureKind.Authentication:
                        SetState(ConnectionState.AuthFailed, MailConnector.Describe(ex));
                        await WaitQuietly(TimeSpan.FromMinutes(30), ct);
                        break;
                    case FailureKind.Certificate:
                        SetState(ConnectionState.Error, MailConnector.Describe(ex));
                        await WaitQuietly(TimeSpan.FromMinutes(30), ct);
                        break;
                    default:
                        var wasOnline = State is ConnectionState.Online or ConnectionState.Degraded;
                        SetState(wasOnline ? ConnectionState.Degraded : ConnectionState.Offline, MailConnector.Describe(ex));
                        await WaitQuietly(backoff.Next(), ct);
                        break;
                }
            }
            finally
            {
                if (imap is not null)
                {
                    try
                    {
                        if (imap.IsConnected)
                        {
                            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await imap.DisconnectAsync(true, quick.Token);
                        }
                    }
                    catch { /* connection already gone */ }
                    imap.Dispose();
                }
            }
        }
        SetState(ConnectionState.Offline, null);
    }

    async Task SessionAsync(ImapClient imap, CancellationToken ct)
    {
        await SyncFolderListAsync(imap, ct);
        while (!ct.IsCancellationRequested)
        {
            _wake.TryConsume();

            await ServeBodyRequestsAsync(imap, ct);
            await ReplayOperationsAsync(imap, ct);
            await ProcessOutboxAsync(imap, ct);

            var poll = TimeSpan.FromMinutes(Math.Max(1, _settings().Sync.PollMinutes));
            IsSyncing = true;
            Raise(ChangeKind.Status);
            if (DateTimeOffset.UtcNow - _lastFullSync >= poll)
            {
                await SyncFolderListAsync(imap, ct);
                await SyncAllFoldersAsync(imap, ct);
                _lastFullSync = DateTimeOffset.UtcNow;
                _inboxDirty = false;
            }
            else
            {
                if (_inboxDirty && _store.FindFolderByRole(_accountId, FolderRole.Inbox) is { } inbox)
                {
                    _inboxDirty = false;
                    await SyncFolderAsync(imap, inbox, ct);
                }
                while (_folderRequests.TryDequeue(out var folderId))
                    if (_store.GetFolder(folderId) is { } requested) await SyncFolderAsync(imap, requested, ct);
            }
            await ServeBodyRequestsAsync(imap, ct);
            await PrefetchAsync(imap, ct);
            IsSyncing = false;
            SetState(ConnectionState.Online, null);

            if (_morePrefetch || _store.GetDueOps(_accountId).Count > 0) continue;
            var wait = poll - (DateTimeOffset.UtcNow - _lastFullSync);
            if (wait > IdleRenewal) wait = IdleRenewal;
            // A mail held back for "Rückgängig" goes out right when its delay is over.
            var nextSend = _store.GetOutbox(_accountId)
                .Where(o => o.State == OutboxState.Queued && o.NextAttempt > DateTimeOffset.UtcNow)
                .Select(o => o.NextAttempt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500))
                .DefaultIfEmpty(wait).Min();
            await IdleAsync(imap, nextSend < wait ? nextSend : wait, ct);
        }
    }

    /// <summary>Waits for new mail (IDLE), a local change, or the timeout — whichever comes first.</summary>
    async Task IdleAsync(ImapClient imap, TimeSpan max, CancellationToken ct)
    {
        if (max <= TimeSpan.Zero) return;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        done.CancelAfter(max);
        _ = _wake.WhenSet.ContinueWith(_ => { try { done.Cancel(); } catch (ObjectDisposedException) { } },
            TaskScheduler.Default);

        var inbox = imap.Inbox;
        void Dirty(object? sender, EventArgs e)
        {
            _inboxDirty = true;
            try { done.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (!_settings().Sync.UsePush || !imap.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, done.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            _inboxDirty = true; // polling server: always look at the inbox again
            return;
        }

        if (!inbox.IsOpen) await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        inbox.CountChanged += Dirty;
        inbox.MessageExpunged += Dirty;
        inbox.MessageFlagsChanged += Dirty;
        try
        {
            await imap.IdleAsync(done.Token, ct);
        }
        finally
        {
            inbox.CountChanged -= Dirty;
            inbox.MessageExpunged -= Dirty;
            inbox.MessageFlagsChanged -= Dirty;
        }
    }

    AccountConfig Account => _store.GetAccount(_accountId)?.Config
        ?? throw new InvalidOperationException("Konto wurde entfernt.");

    async Task<Credentials> GetCredentialsAsync(CancellationToken ct)
    {
        var (credentials, refreshed) = await MailConnector.EnsureFreshAsync(Account, _store.GetCredentials(_accountId), ct);
        if (refreshed) _store.UpdateCredentials(_accountId, credentials);
        return credentials;
    }

    async Task WaitQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct); }
        catch (OperationCanceledException) { }
    }

    void FailBodyRequests()
    {
        while (_bodyRequests.TryDequeue(out var request)) request.Result.TrySetResult(null);
    }

    void SetState(ConnectionState state, string? text)
    {
        if (State == state && StatusText == text) return;
        State = state;
        StatusText = text;
        Raise(ChangeKind.Status);
    }

    void Raise(ChangeKind kind, long? folderId = null, string? text = null) =>
        Changed?.Invoke(new WorkerChange(_accountId, kind, folderId, text));

    public async ValueTask DisposeAsync()
    {
        _net.Changed -= Wake;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* shutting down */ }
        }
        _cts.Dispose();
    }
}
