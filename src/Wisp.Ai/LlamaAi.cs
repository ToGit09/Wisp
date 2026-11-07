using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Wisp.Core;
using Wisp.Core.Smart;

namespace Wisp.Ai;

/// <summary>
/// Wisp's local language model: Qwen3 1.7B (Apache 2.0) via llama.cpp, on the CPU. The model is
/// downloaded once from Hugging Face, checked against a fixed SHA-256 and then used fully offline.
/// Mails are never sent anywhere. The model is loaded on first use and unloaded after a few idle
/// minutes, so it only takes memory while it is working.
/// </summary>
public sealed class LlamaAi : ILocalAi
{
    // Pinned revision and checksum: a changed or tampered file is rejected.
    const string ModelFile = "Qwen3-1.7B-Q4_K_M.gguf";
    const string ModelUrl = "https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/d7f544eead698dbd1f15126ef60b45a1e1933222/" + ModelFile;
    const string ModelSha256 = "b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897";
    const long ModelBytes = 1_107_409_472;
    const uint ContextTokens = 3072;
    static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    readonly string _dir;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Timer _idleTimer;
    LLamaWeights? _weights;
    ModelParams? _params;
    CancellationTokenSource? _download;

    static LlamaAi()
    {
        // llama.cpp is chatty on stderr; Wisp writes no logs about mail processing.
        LLama.Native.NativeLibraryConfig.All.WithLogCallback((_, _) => { });
    }

    public LlamaAi(string? modelDir = null)
    {
        _dir = modelDir ?? AppPaths.Ensure(Path.Combine(AppPaths.DataDir, "models"));
        _idleTimer = new Timer(_ => _ = UnloadIfIdleAsync());
        State = File.Exists(ModelPath) ? AiState.Ready : AiState.NotInstalled;
    }

    string ModelPath => Path.Combine(_dir, ModelFile);

    public AiState State { get; private set; }
    public double Progress { get; private set; }
    public string? StatusText { get; private set; }
    public string ModelDescription => $"Qwen3 1.7B, Apache-2.0-Lizenz ({ModelBytes / 1e9:0.0} GB)";
    public event Action? StateChanged;

    void SetState(AiState state, string? text = null, double progress = 0)
    {
        State = state;
        StatusText = text;
        Progress = progress;
        StateChanged?.Invoke();
    }

    // ───────────────────────── Download ─────────────────────────

    public async Task DownloadAsync(CancellationToken ct)
    {
        if (State is AiState.Ready or AiState.Downloading) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _download = cts;
        var part = ModelPath + ".part";
        try
        {
            SetState(AiState.Downloading, "Lade Modell …");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Wisp-Mail");

            // Resume an interrupted download instead of starting over.
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > ModelBytes) { File.Delete(part); have = 0; }
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored the range

            await using (var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[1 << 16];
                var done = have;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    done += read;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                    {
                        lastReport = DateTime.UtcNow;
                        SetState(AiState.Downloading, $"{done / 1e6:0} von {ModelBytes / 1e6:0} MB", (double)done / ModelBytes);
                    }
                }
            }

            SetState(AiState.Downloading, "Prüfe Modell …", 1);
            if (!await VerifyAsync(part, cts.Token))
            {
                File.Delete(part);
                SetState(AiState.Error, "Die geladene Datei ist beschädigt oder verändert und wurde gelöscht. Bitte erneut versuchen.");
                return;
            }
            File.Move(part, ModelPath, overwrite: true);
            AppPaths.RestrictFile(ModelPath);
            SetState(AiState.Ready);
        }
        catch (OperationCanceledException)
        {
            SetState(AiState.NotInstalled, "Download abgebrochen. Er wird beim nächsten Mal fortgesetzt.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            SetState(AiState.Error, "Das Modell konnte nicht geladen werden: " + ex.Message);
        }
        finally
        {
            _download = null;
        }
    }

    public void CancelDownload()
    {
        try { _download?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    static async Task<bool> VerifyAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash) == ModelSha256;
    }

    public async Task DeleteAsync()
    {
        CancelDownload();
        await _gate.WaitAsync();
        try
        {
            Unload();
            foreach (var file in new[] { ModelPath, ModelPath + ".part" })
                if (File.Exists(file)) File.Delete(file);
            SetState(AiState.NotInstalled);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────── Inference ─────────────────────────

    const string SummaryInstruction =
        "Du fasst E-Mails für den Empfänger auf Deutsch zusammen. Schreibe höchstens drei kurze Sätze ohne Einleitung. " +
        "Nenne, was der Absender will, und – nur falls vorhanden – was zu tun ist (Frist, Termin, Betrag). Erfinde nichts.";

    const string AnalysisInstruction =
        "Du sortierst E-Mails und fasst sie auf Deutsch zusammen. Antworte genau in diesem Format und sonst nichts:\n" +
        "KATEGORIE: wichtig | werbung | spam | normal\n" +
        "ZUSAMMENFASSUNG: höchstens zwei kurze Sätze\n\n" +
        "wichtig = persönliche Nachricht oder etwas, das der Empfänger wissen oder tun muss (Rechnung, Termin, Frist, Konto, Lieferung).\n" +
        "werbung = Newsletter, Angebote, Rabatte, Marketing.\n" +
        "spam = unerwünscht, Betrug, Phishing, gefälschter Absender.\n" +
        "normal = alles andere.";

    public async Task<AiAnalysis?> AnalyzeAsync(AiMail mail, CancellationToken ct)
    {
        var answer = new StringBuilder();
        await foreach (var piece in RunAsync(AnalysisInstruction, Describe(mail, withHints: true), 160, interactive: false, ct)) answer.Append(piece);
        return answer.Length == 0 && State != AiState.Ready ? null : ParseAnalysis(answer.ToString());
    }

    public IAsyncEnumerable<string> SummarizeAsync(AiMail mail, CancellationToken ct) =>
        RunAsync(SummaryInstruction, Describe(mail, withHints: false), 220, interactive: true, ct);

    static string Describe(AiMail mail, bool withHints)
    {
        var sb = new StringBuilder();
        sb.Append("Von: ").AppendLine(mail.From);
        sb.Append("Betreff: ").AppendLine(mail.Subject);
        if (withHints && mail.Hints.Length > 0) sb.Append("Hinweise: ").AppendLine(mail.Hints);
        sb.AppendLine().Append(mail.Text);
        return sb.ToString();
    }

    /// <summary>Parses "KATEGORIE: …\nZUSAMMENFASSUNG: …" leniently; small models are not always tidy.</summary>
    public static AiAnalysis ParseAnalysis(string answer)
    {
        MailCategory? category = null;
        var summary = "";
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim().TrimStart('*', '-', ' ');
            if (line.StartsWith("KATEGORIE", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim().ToLowerInvariant();
                category = value.StartsWith("wichtig") ? MailCategory.Important
                    : value.StartsWith("werbung") ? MailCategory.Promotion
                    : value.StartsWith("spam") ? MailCategory.SuspectedSpam
                    : value.StartsWith("normal") ? MailCategory.None
                    : null;
            }
            else if (line.StartsWith("ZUSAMMENFASSUNG", StringComparison.OrdinalIgnoreCase))
            {
                summary = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (summary.Length > 0 && line.Length > 0)
            {
                summary += " " + line;
            }
        }
        return new AiAnalysis(summary, category);
    }

    /// <param name="interactive">Someone is waiting (on-demand summary): use more cores. Background work stays at half.</param>
    async IAsyncEnumerable<string> RunAsync(string instruction, string content, int maxTokens, bool interactive, [EnumeratorCancellation] CancellationToken ct)
    {
        if (State != AiState.Ready) yield break;
        await _gate.WaitAsync(ct);
        try
        {
            var (weights, parameters) = await LoadAsync(ct);
            var threads = interactive ? Math.Clamp(Environment.ProcessorCount - 1, 2, 16) : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var context = new ModelParams(parameters.ModelPath)
            {
                ContextSize = parameters.ContextSize,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
                BatchSize = parameters.BatchSize,
            };
            var executor = new StatelessExecutor(weights, context) { ApplyTemplate = false };
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopK = 20, RepeatPenalty = 1.1f },
            };
            var inThink = false;
            await foreach (var piece in executor.InferAsync(Prompt(instruction, content), inference, ct))
            {
                // Qwen3 may still open a "thinking" block; it is not part of the answer.
                if (piece.Contains("<think>")) { inThink = true; continue; }
                if (inThink) { if (piece.Contains("</think>")) inThink = false; continue; }
                var clean = piece.Replace("<|im_end|>", "").Replace("<|endoftext|>", "");
                if (clean.Length > 0) yield return clean;
            }
        }
        finally
        {
            _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>ChatML with an empty thinking block: Qwen3 answers directly ("no think" mode).</summary>
    string Prompt(string instruction, string content)
    {
        // Keep the mail within the context: roughly 3.5 characters per token, leaving room for the answer.
        var budget = (int)((ContextTokens - 400) * 3.2);
        if (content.Length > budget) content = content[..budget] + " …";
        return $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n{content}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
    }

    async Task<(LLamaWeights, ModelParams)> LoadAsync(CancellationToken ct)
    {
        if (_weights is not null && _params is not null) return (_weights, _params);
        // Leave cores for the rest of the system: the model works in the background.
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        _params = new ModelParams(ModelPath)
        {
            ContextSize = ContextTokens,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
            BatchSize = 512,
        };
        _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
        return (_weights, _params);
    }

    async Task UnloadIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // busy: the next run restarts the timer
        try { Unload(); }
        finally { _gate.Release(); }
    }

    void Unload()
    {
        _weights?.Dispose();
        _weights = null;
        _params = null;
    }

    public async ValueTask DisposeAsync()
    {
        CancelDownload();
        await _idleTimer.DisposeAsync();
        await _gate.WaitAsync(TimeSpan.FromSeconds(5));
        Unload();
    }
}
