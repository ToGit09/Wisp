namespace Wisp.Core.Smart;

public enum AiState
{
    /// <summary>The model is not on this computer yet.</summary>
    NotInstalled,
    Downloading,
    /// <summary>Downloaded and checked; loaded on first use.</summary>
    Ready,
    Error,
}

/// <summary>What the local model gets to see of a mail: never more than this, and it never leaves the computer.</summary>
public sealed record AiMail(string From, string Subject, string Text, string Hints);

/// <summary>Result of the background pass over a new mail.</summary>
public sealed record AiAnalysis(string Summary, MailCategory? Category);

/// <summary>
/// The local language model of the AI edition (implemented in Wisp.Ai). The light edition has none:
/// <see cref="MailService.Ai"/> stays null and every AI feature is simply absent.
/// </summary>
public interface ILocalAi : IAsyncDisposable
{
    AiState State { get; }
    /// <summary>Download progress 0–1 while <see cref="AiState.Downloading"/>; otherwise an explanation in <see cref="StatusText"/>.</summary>
    double Progress { get; }
    string? StatusText { get; }
    /// <summary>Human readable model name and size, e.g. "Qwen3 1.7B (1,1 GB)".</summary>
    string ModelDescription { get; }
    event Action? StateChanged;

    /// <summary>Downloads and verifies the model (once). Safe to call again after an error.</summary>
    Task DownloadAsync(CancellationToken ct);
    /// <summary>Stops a running download; the part already loaded is kept and resumed next time.</summary>
    void CancelDownload();
    /// <summary>Removes the model from the disk.</summary>
    Task DeleteAsync();

    /// <summary>Background pass for a new mail: short summary and a category. Null when the model is unavailable.</summary>
    Task<AiAnalysis?> AnalyzeAsync(AiMail mail, CancellationToken ct);
    /// <summary>On demand ("Zusammenfassen"): the summary, piece by piece as it is written.</summary>
    IAsyncEnumerable<string> SummarizeAsync(AiMail mail, CancellationToken ct);
}
