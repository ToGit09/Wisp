using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wisp.Core.Settings;
using Wisp.Core.Smart;

namespace Wisp.App.ViewModels;

/// <summary>Wisp 2: "Sortierung &amp; KI" – edition, one-time codes, inbox tabs, spam detection, local model.</summary>
public sealed partial class SettingsViewModel
{
    SmartSettings S => _s.Smart;

    // ───────────────────────── Edition ─────────────────────────

    public bool IsAiEdition => Edition.IsAi;
    public string EditionText => Edition.IsAi
        ? "Sie nutzen die AIEdition: alle Helfer plus ein lokales KI-Modell für Zusammenfassungen und genauere Sortierung."
        : "Sie nutzen die LightEdition: schnell und schlank, ohne KI-Modell. Codes, Reiter und Spamerkennung funktionieren trotzdem.";
    public string SwitchEditionLabel => "Zur " + Edition.NameOf(!Edition.IsAi) + " wechseln";

    [ObservableProperty] public partial string? EditionStatus { get; set; }
    [ObservableProperty] public partial bool IsSwitchingEdition { get; set; }
    [ObservableProperty] public partial int SwitchProgress { get; set; }

    [RelayCommand]
    async Task SwitchEdition()
    {
        var toAi = !Edition.IsAi;
        var text = toAi
            ? "Wisp lädt die AIEdition (etwa 100 MB) und startet neu. Danach wird einmalig das KI-Modell geladen (etwa 1,1 GB). Ihre Mails und Einstellungen bleiben erhalten."
            : "Wisp lädt die LightEdition und startet neu. Ihre Mails und Einstellungen bleiben erhalten. Das KI-Modell können Sie vorher hier löschen, um Platz zu sparen.";
        if (!await _dialogs.ConfirmAsync(SwitchEditionLabel + "?", text, "Wechseln")) return;

        IsSwitchingEdition = true;
        EditionStatus = $"Lade die {Edition.NameOf(toAi)} …";
        var outcome = await Updater.SwitchEditionAsync(toAi, p => Dispatcher.UIThread.Post(() => SwitchProgress = p), CancellationToken.None);
        IsSwitchingEdition = false;
        EditionStatus = Updater.Describe(outcome, toAi);
        if (outcome == SwitchOutcome.Restarting && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    // ───────────────────────── Helpers ─────────────────────────

    public bool CopyCodes { get => S.CopyCodes; set => Set(S.CopyCodes, value, v => S.CopyCodes = v); }

    public IReadOnlyList<Choice<int>> ClearCodeChoices { get; } =
        [new("Nach 1 Minute", 60), new("Nach 2 Minuten", 120), new("Nach 5 Minuten", 300), new("Nie", 0)];

    public Choice<int> ClearCodeAfter
    {
        get => ClearCodeChoices.FirstOrDefault(c => c.Value == S.ClearCodeAfterSeconds) ?? ClearCodeChoices[1];
        set { if (value is not null) Set(S.ClearCodeAfterSeconds, value.Value, v => S.ClearCodeAfterSeconds = v); }
    }

    public bool Categories
    {
        get => S.Categories;
        set { if (Set(S.Categories, value, v => S.Categories = v)) _mail.Resort(); }
    }

    public bool SpamDetection
    {
        get => S.SpamDetection;
        set
        {
            if (!Set(S.SpamDetection, value, v => S.SpamDetection = v)) return;
            _mail.Resort();
            OnPropertyChanged(nameof(SpamFilterText));
        }
    }

    public string SpamFilterText => _mail.SpamFilterTrained
        ? $"Der Wortfilter hat aus {_mail.SpamFilterSamples} Mails in Ihren Spam-Ordnern gelernt und lernt weiter, wenn Sie Mails als Spam markieren."
        : "Der Wortfilter lernt, sobald mindestens 15 Mails in Ihren Spam-Ordnern liegen. Bis dahin prüft Wisp Absender und Kopfzeilen.";

    [ObservableProperty] public partial int SenderRuleCount { get; set; }
    public bool HasSenderRules => SenderRuleCount > 0;
    partial void OnSenderRuleCountChanged(int value) => OnPropertyChanged(nameof(HasSenderRules));
    public string SenderRuleText => SenderRuleCount == 1 ? "1 Absender-Regel" : $"{SenderRuleCount} Absender-Regeln";

    [RelayCommand]
    async Task ClearSenderRules()
    {
        if (!await _dialogs.ConfirmAsync("Absender-Regeln löschen?",
                "Alle Regeln „Absender immer einordnen als …“ werden gelöscht. Die Mails werden danach wieder automatisch sortiert.", "Löschen", danger: true))
            return;
        _mail.ClearSenderRules();
        SenderRuleCount = 0;
        OnPropertyChanged(nameof(SenderRuleText));
    }

    // ───────────────────────── Local AI (AIEdition) ─────────────────────────

    ILocalAi? Ai => _mail.Ai;

    public bool AiEnabled
    {
        get => S.AiEnabled;
        set
        {
            if (!Set(S.AiEnabled, value, v => S.AiEnabled = v)) return;
            if (!value) Ai?.CancelDownload();
            _mail.Resort();
            RefreshAi();
        }
    }

    public bool AiBackground { get => S.AiBackground; set => Set(S.AiBackground, value, v => S.AiBackground = v); }

    public string ModelDescription => Ai?.ModelDescription ?? "";
    public string AiStateText => Ai is null ? "" : Ai.State switch
    {
        AiState.Ready => "Das Modell ist installiert und arbeitet nur auf diesem Computer.",
        AiState.Downloading => "Wird geladen: " + (Ai.StatusText ?? ""),
        AiState.Error => Ai.StatusText ?? "Fehler beim Laden des Modells.",
        _ => Ai.StatusText ?? "Das Modell ist noch nicht geladen.",
    };
    public bool CanDownloadModel => Ai is { State: AiState.NotInstalled or AiState.Error } && S.AiEnabled;
    public bool IsDownloadingModel => Ai?.State == AiState.Downloading;
    public bool CanDeleteModel => Ai?.State == AiState.Ready;
    public double ModelProgress => (Ai?.Progress ?? 0) * 100;

    void HookAi()
    {
        SenderRuleCount = _mail.CountSenderRules();
        if (Ai is not null) Ai.StateChanged += OnAiStateChanged;
    }

    /// <summary>Called when the window closes, so the model does not keep the view model alive.</summary>
    public void Detach()
    {
        if (Ai is not null) Ai.StateChanged -= OnAiStateChanged;
    }

    void OnAiStateChanged() => Dispatcher.UIThread.Post(RefreshAi);

    void RefreshAi()
    {
        OnPropertyChanged(nameof(AiStateText));
        OnPropertyChanged(nameof(CanDownloadModel));
        OnPropertyChanged(nameof(IsDownloadingModel));
        OnPropertyChanged(nameof(CanDeleteModel));
        OnPropertyChanged(nameof(ModelProgress));
    }

    [RelayCommand]
    async Task DownloadModel()
    {
        if (Ai is null) return;
        await Ai.DownloadAsync(CancellationToken.None);
        RefreshAi();
    }

    [RelayCommand]
    void CancelModelDownload() => Ai?.CancelDownload();

    [RelayCommand]
    async Task DeleteModel()
    {
        if (Ai is null) return;
        if (!await _dialogs.ConfirmAsync("KI-Modell löschen?",
                "Das Modell wird von diesem Computer entfernt (etwa 1,1 GB). Vorhandene Zusammenfassungen bleiben. Sie können es jederzeit erneut laden.",
                "Löschen", danger: true))
            return;
        await Ai.DeleteAsync();
        RefreshAi();
    }
}
