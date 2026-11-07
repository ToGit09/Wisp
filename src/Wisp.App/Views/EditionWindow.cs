using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wisp.App.Views;

/// <summary>
/// First start of Wisp 2 (fresh install or upgrade from Wisp 1): "LightEdition or AIEdition?".
/// The same setup installs the light edition; choosing AI downloads the AI edition right here and restarts.
/// Closes with true when Wisp should continue starting, false when it restarts into the other edition.
/// </summary>
public sealed class EditionWindow : Window
{
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, IsVisible = false, Height = 6 };
    readonly Button _continue = new() { Content = "Weiter", Classes = { "primary" }, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
    readonly RadioButton _light = new() { GroupName = "edition" };
    readonly RadioButton _ai = new() { GroupName = "edition" };
    CancellationTokenSource? _cts;

    public bool ContinueStart { get; private set; } = true;

    public EditionWindow()
    {
        Title = "Willkommen bei Wisp 2";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Wisp/Assets/wisp.ico")));

        _light.Content = Option("LightEdition",
            "Schnell und schlank wie bisher. Dazu neu: Einmalcodes werden automatisch kopiert, der Posteingang wird in " +
            "„Wichtig“, „Werbung“ und „Spamverdacht“ sortiert, und eine erweiterte Spamerkennung lernt aus Ihrem Spam-Ordner.");
        _ai.Content = Option("AIEdition",
            "Alles aus der LightEdition, plus ein lokales KI-Modell: Es fasst Mails zusammen (neue im Hintergrund, ältere auf Knopfdruck) " +
            "und sortiert neue Mails genauer. Läuft komplett auf diesem Computer, ohne Cloud. Einmaliger Download von etwa 1,1 GB; " +
            "braucht beim Arbeiten etwa 2 GB Arbeitsspeicher.");
        (Edition.IsAi ? _ai : _light).IsChecked = true;
        _continue.Click += async (_, _) => await ContinueAsync();

        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Welche Wisp-Edition möchten Sie nutzen?", Classes = { "title" } },
                new TextBlock
                {
                    Text = "Beide sind kostenlos und senden nichts an Dritte. Sie können die Edition später unter Einstellungen → Sortierung & KI wechseln.",
                    Classes = { "hint" },
                },
                Card(_light),
                Card(_ai),
                _progress,
                _status,
                _continue,
            },
        };
        Closing += (_, _) => _cts?.Cancel();
    }

    static Border Card(RadioButton radio) => new()
    {
        Classes = { "card" },
        Padding = new Thickness(14, 10),
        Child = radio,
    };

    static StackPanel Option(string title, string text) => new()
    {
        Spacing = 4,
        Margin = new Thickness(6, 0, 0, 0),
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = text, Classes = { "hint" }, MaxWidth = 440 },
        },
    };

    async Task ContinueAsync()
    {
        var wantAi = _ai.IsChecked == true;
        if (wantAi == Edition.IsAi)
        {
            Close();
            return;
        }

        _continue.IsEnabled = _light.IsEnabled = _ai.IsEnabled = false;
        _progress.IsVisible = true;
        _status.Text = $"Lade die {Edition.NameOf(wantAi)} …";
        _cts = new CancellationTokenSource();
        try
        {
            var outcome = await Updater.SwitchEditionAsync(wantAi,
                p => Dispatcher.UIThread.Post(() => _progress.Value = p), _cts.Token);
            if (outcome == SwitchOutcome.Restarting)
            {
                ContinueStart = false;
                Close();
                return;
            }
            _status.Text = Updater.Describe(outcome, wantAi) + $" Wisp startet jetzt als {Edition.Name}.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _progress.IsVisible = false;
        _continue.Content = "Mit der " + Edition.Name + " weiter";
        _continue.IsEnabled = true;
        _light.IsEnabled = _ai.IsEnabled = true;
        (Edition.IsAi ? _ai : _light).IsChecked = true;
    }
}
