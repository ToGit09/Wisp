using System.Runtime.CompilerServices;
using Wisp.Core.Rendering;
using Wisp.Core.Settings;
using Wisp.Core.Storage;
using Wisp.Core.Sync;

namespace Wisp.Core.Smart;

/// <summary>
/// Wisp 2 helpers running next to the sync: sorts the inbox into tabs, spots one-time codes and – in the
/// AI edition – lets the local model summarise and sort new mails. Two loops: a quick one for rules and
/// codes, and a slow one for the model, so a busy model never delays a code.
/// </summary>
public sealed class SmartService : IAsyncDisposable
{
    /// <summary>A code is only copied from mails this fresh: older ones are not what the user is waiting for.</summary>
    public static readonly TimeSpan CodeWindow = TimeSpan.FromMinutes(10);
    /// <summary>The AI handles new mails of this period in the background; older ones only on request.</summary>
    static readonly TimeSpan AiWindow = TimeSpan.FromDays(2);
    const int TextLimit = 6000;

    readonly MailStore _store;
    readonly Func<AppSettings> _settings;
    readonly Action<WorkerChange> _raise;
    readonly AsyncSignal _wake = new();
    readonly AsyncSignal _aiWake = new();
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<long, IReadOnlySet<string>> _contacts = [];
    readonly Dictionary<long, IReadOnlyDictionary<string, MailCategory>> _senderRules = [];
    readonly object _cacheGate = new();
    SpamFilter? _filter;
    long _trainingStamp = -1;
    DateTimeOffset _contactsLoaded = DateTimeOffset.MinValue;
    Task? _rulesLoop, _aiLoop;
    int _userWaiting;
    volatile CancellationTokenSource? _backgroundRun;

    public SmartService(MailStore store, Func<AppSettings> settings, Action<WorkerChange> raise)
    {
        _store = store;
        _settings = settings;
        _raise = raise;
    }

    public ILocalAi? Ai { get; set; }

    public void Start()
    {
        _store.MarkCodesCheckedBefore(DateTimeOffset.UtcNow - CodeWindow);
        _rulesLoop ??= Task.Run(() => RulesLoopAsync(_cts.Token));
        _aiLoop ??= Task.Run(() => AiLoopAsync(_cts.Token));
    }

    /// <summary>Something arrived or moved: look again soon.</summary>
    public void Poke()
    {
        _wake.Set();
        _aiWake.Set();
    }

    /// <summary>Sender rules or settings changed: forget cached knowledge and sort everything again.</summary>
    public void Resort(bool includeAi = false)
    {
        lock (_cacheGate)
        {
            _senderRules.Clear();
            _contacts.Clear();
            _trainingStamp = -1;
        }
        _store.ResetClassification(includeAi: includeAi);
        Poke();
    }

    public int SpamSamples => _filter?.SpamSamples ?? 0;
    public bool FilterTrained => _filter?.IsTrained == true;

    // ───────────────────────── rules & codes ─────────────────────────

    async Task RulesLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _settings().Smart;
                if (s.CopyCodes) FindCodes();
                if (s.Categories || s.SpamDetection) ClassifyPending(s);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken mail must not stop the helpers; the next round tries again.
                System.Diagnostics.Trace.WriteLine(ex);
            }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    void ClassifyPending(SmartSettings s)
    {
        RetrainIfNeeded();
        for (var round = 0; round < 20; round++)
        {
            var queue = _store.GetClassifyQueue(500);
            if (queue.Count == 0) return;
            var results = queue.Select(c => (c.Id, Classify(c, s).Category, ClassifiedBy.Rules)).ToList();
            _store.SetClassification(results);
            foreach (var account in queue.Select(c => c.AccountId).Distinct())
                _raise(new WorkerChange(account, ChangeKind.Messages));
            _aiWake.Set();
        }
    }

    Classification Classify(ClassifyCandidate c, SmartSettings s)
    {
        var contacts = ContactsOf(c.AccountId);
        var rules = SenderRulesOf(c.AccountId);
        MailCategory? rule = rules.TryGetValue(_store.HashSender(c.Envelope.FromAddress), out var r) ? r : null;
        var known = contacts.Contains(c.Envelope.FromAddress.ToLowerInvariant()) || c.Flags.HasFlag(MessageFlags.Answered);
        var result = Categorizer.Classify(new ClassifyInput(c.Envelope, c.Signals, c.Flags, known, rule), _filter, s.SpamDetection);
        // Without spam detection there is no "Spamverdacht" tab content from the word filter or header checks.
        if (!s.SpamDetection && result.Category == MailCategory.SuspectedSpam && rule is null) result = result with { Category = MailCategory.None };
        return result;
    }

    void RetrainIfNeeded()
    {
        var stamp = _store.TrainingStamp();
        if (stamp == _trainingStamp) return;
        var samples = _store.GetTrainingSamples(2000);
        _filter = SpamFilter.Train(samples.Select(x => (SpamFilter.TextOf(x.Envelope, x.Signals), x.Spam)));
        _trainingStamp = stamp;
    }

    IReadOnlySet<string> ContactsOf(long accountId)
    {
        lock (_cacheGate)
        {
            if (DateTimeOffset.UtcNow - _contactsLoaded > TimeSpan.FromMinutes(10))
            {
                _contacts.Clear();
                _contactsLoaded = DateTimeOffset.UtcNow;
            }
            if (!_contacts.TryGetValue(accountId, out var set)) _contacts[accountId] = set = _store.GetContacts(accountId);
            return set;
        }
    }

    IReadOnlyDictionary<string, MailCategory> SenderRulesOf(long accountId)
    {
        lock (_cacheGate)
        {
            if (!_senderRules.TryGetValue(accountId, out var rules)) _senderRules[accountId] = rules = _store.GetSenderRules(accountId);
            return rules;
        }
    }

    void FindCodes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var c in _store.GetCodeCandidates(now - CodeWindow))
        {
            var code = OneTimeCode.Find(c.Envelope.Subject, c.Envelope.Preview);
            if (code is null && c.HasBody) code = OneTimeCode.Find(null, PlainTextOf(c.Id, quotes: false));
            // Without content yet, wait for it (the sync downloads fresh mails right away).
            if (code is null && !c.HasBody && now - c.Date < CodeWindow) continue;
            _store.MarkCodeChecked(c.Id);
            if (code is not null) _raise(new WorkerChange(c.AccountId, ChangeKind.Code, Text: code, MessageId: c.Id));
        }
    }

    string PlainTextOf(long messageId, bool quotes)
    {
        if (_store.GetBody(messageId) is not { } raw) return "";
        var mail = MailRenderer.Render(raw, new RenderOptions(ShowHtml: false, LoadRemoteImages: false));
        var blocks = quotes ? mail.Blocks : mail.Blocks.Where(b => b.Kind != BlockKind.Quote);
        var text = MailRenderer.ToPlainText(blocks);
        return text.Length <= TextLimit ? text : text[..TextLimit];
    }

    // ───────────────────────── local AI ─────────────────────────

    async Task AiLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _settings().Smart;
                if (Ai is { State: AiState.Ready } ai && s.AiEnabled && s.AiBackground) await AnalyzeNewAsync(ai, s, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(ex);
            }
            try { await _aiWake.WaitAsync(TimeSpan.FromMinutes(2), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task AnalyzeNewAsync(ILocalAi ai, SmartSettings s, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Someone is waiting for an on-demand summary: let it go first.
            while (Volatile.Read(ref _userWaiting) > 0) await Task.Delay(500, ct);
            var next = _store.GetAiQueue(DateTimeOffset.UtcNow - AiWindow, 1).FirstOrDefault();
            if (next is null) return;

            var mail = ToAiMail(next.Id, next.Envelope, next.Signals);
            AiAnalysis? result;
            using (var run = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                _backgroundRun = run;
                try { result = await ai.AnalyzeAsync(mail, run.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; } // made way for the user
                finally { _backgroundRun = null; }
            }
            if (result is null) return; // model unavailable: try again later

            if (result.Summary.Length > 0)
            {
                _store.SaveSummary(next.Id, result.Summary);
                _raise(new WorkerChange(next.AccountId, ChangeKind.Summary, MessageId: next.Id));
            }
            // The user's own sender rules and contacts always win over the model.
            var rules = Classify(next, s);
            var known = ContactsOf(next.AccountId).Contains(next.Envelope.FromAddress.ToLowerInvariant());
            var hasRule = SenderRulesOf(next.AccountId).ContainsKey(_store.HashSender(next.Envelope.FromAddress));
            var category = hasRule || known || result.Category is null ? rules.Category : result.Category.Value;
            if (!s.SpamDetection && category == MailCategory.SuspectedSpam) category = rules.Category;
            _store.SetClassification([(next.Id, category, ClassifiedBy.Ai)]);
            _raise(new WorkerChange(next.AccountId, ChangeKind.Messages));
        }
    }

    AiMail ToAiMail(long messageId, Envelope e, MailSignals signals)
    {
        var hints = new List<string>();
        if (signals.HasFlag(MailSignals.Newsletter) || signals.HasFlag(MailSignals.Bulk)) hints.Add("Massenversand/Newsletter");
        if (signals.HasFlag(MailSignals.Automated)) hints.Add("automatisch versendet");
        if (signals.HasFlag(MailSignals.AuthFailed)) hints.Add("Absenderprüfung (SPF/DKIM) fehlgeschlagen");
        if (signals.HasFlag(MailSignals.NameSpoof)) hints.Add("Absendername passt nicht zur Adresse");
        if (signals.HasFlag(MailSignals.ServerSpam)) hints.Add("vom Mailserver als Spam markiert");
        if (signals.HasFlag(MailSignals.DirectlyAddressed)) hints.Add("direkt an den Empfänger");
        var from = string.IsNullOrWhiteSpace(e.FromName) ? e.FromAddress : $"{e.FromName} <{e.FromAddress}>";
        var text = PlainTextOf(messageId, quotes: false);
        return new AiMail(from, e.Subject, text.Length > 0 ? text : e.Preview, string.Join(", ", hints));
    }

    /// <summary>On demand: streams a summary and stores it when complete. Takes priority over the background pass.</summary>
    public async IAsyncEnumerable<string> SummarizeAsync(long messageId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Ai is not { State: AiState.Ready } ai || _store.GetMessage(messageId) is not { } item) yield break;
        var (signals, _) = _store.GetSortingInfo(messageId);
        var mail = ToAiMail(messageId, item.Envelope, signals);
        var full = new System.Text.StringBuilder();
        Interlocked.Increment(ref _userWaiting);
        try { _backgroundRun?.Cancel(); }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
        try
        {
            await foreach (var piece in ai.SummarizeAsync(mail, ct))
            {
                full.Append(piece);
                yield return piece;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _userWaiting);
        }
        var summary = full.ToString().Trim();
        if (summary.Length > 0 && !ct.IsCancellationRequested)
        {
            _store.SaveSummary(messageId, summary);
            _raise(new WorkerChange(item.AccountId, ChangeKind.Summary, MessageId: messageId));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var loop in new[] { _rulesLoop, _aiLoop })
        {
            if (loop is null) continue;
            try { await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* shutting down */ }
        }
        _cts.Dispose();
    }
}
