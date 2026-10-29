using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TheArtOfDev.HtmlRenderer.Avalonia;
using Wisp.App.ViewModels;
using Wisp.Core.Mail;

namespace Wisp.App.Views;

public partial class ComposeWindow : Window
{
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(p => Path.GetFileName(p ?? ""));

    // Text colours offered by the colour button (readable on white).
    static readonly string[] TextColors = ["#1D1F24", "#6A6F7C", "#D64545", "#E07A1F", "#2E9D57", "#0F9D9A", "#4F6BED", "#7C5CFA", "#D6458F"];

    readonly ComposeViewModel _vm;
    readonly DispatcherTimer _previewTimer;
    readonly HtmlLabel _preview = new()
    {
        AutoSize = false,
        AutoSizeHeightOnly = true,
        Background = Brushes.White,
        IsContextMenuEnabled = false,
    };
    bool _closingConfirmed;

    public ComposeWindow() : this(null!) { }

    public ComposeWindow(ComposeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => UpdatePreview());
        if (vm is null) return;

        vm.Sent += (_, _) =>
        {
            _closingConfirmed = true;
            Close();
        };
        AttachButton.Click += async (_, _) => await PickAttachmentsAsync();
        DiscardButton.Click += (_, _) => Close();
        // Tunnel, so the text boxes do not turn Ctrl+Enter into a line break first.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && vm.CtrlEnterSends)
            {
                e.Handled = true;
                vm.SendCommand.Execute(null);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += async (_, e) =>
        {
            if (_closingConfirmed || !vm.IsDirty || !vm.ConfirmDiscard) return;
            e.Cancel = true;
            if (await ConfirmDialog.ShowAsync(this, "Entwurf verwerfen?", "Die Nachricht wurde noch nicht gesendet und geht verloren.", "Verwerfen", true))
            {
                _closingConfirmed = true;
                Close();
            }
        };

        // Drag & drop files to attach them.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null) vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        });

        SetUpAdvancedEditor();

        Opened += (_, _) =>
        {
            if (vm.IsAdvanced) EnsureWideEnough();
            if (string.IsNullOrEmpty(vm.To)) return;
            var box = vm.IsAdvanced ? SourceBox : BodyBox;
            box.Focus();
            box.CaretIndex = 0; // replies: start typing above the quote
        };
    }

    async Task PickAttachmentsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Dateien anhängen", AllowMultiple = true });
        _vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    // ───────────────────────── Advanced editor ─────────────────────────

    void SetUpAdvancedEditor()
    {
        PreviewHost.Content = _preview;
        // The preview is the user's own mail: never fetch anything from the internet for it.
        _preview.ImageLoad += (_, e) => { e.Event.Handled = true; e.Event.Callback(); };
        _preview.StylesheetLoad += (_, e) => e.Event.SetStyleSheet = "";
        _preview.LinkClicked += (_, e) => e.Event.Handled = true;

        foreach (var hex in TextColors)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(3), CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.Parse(hex)), Tag = hex,
            };
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) =>
            {
                Apply(t => HtmlEditing.Color(t.Text, t.Start, t.End, hex));
                ((FormatBar.Children.OfType<Button>().First(b => (string?)b.Tag == "color")).Flyout as Flyout)?.Hide();
            };
            ColorPanel.Children.Add(swatch);
        }

        foreach (var button in FormatBar.Children.OfType<Button>())
            if ((string?)button.Tag is { } action and not "color")
                button.Click += async (_, _) => await FormatAsync(action);

        SourceBox.KeyDown += async (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var action = e.Key switch { Key.B => "bold", Key.I => "italic", Key.U => "underline", Key.K => "link", _ => null };
            if (action is null) return;
            e.Handled = true;
            await FormatAsync(action);
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeViewModel.HtmlSource)) SchedulePreview();
            if (e.PropertyName == nameof(ComposeViewModel.IsAdvanced) && _vm.IsAdvanced)
            {
                EnsureWideEnough();
                UpdatePreview();
            }
        };
        UpdatePreview();
    }

    async Task FormatAsync(string action)
    {
        switch (action)
        {
            case "bold": Apply(t => HtmlEditing.Bold(t.Text, t.Start, t.End)); break;
            case "italic": Apply(t => HtmlEditing.Italic(t.Text, t.Start, t.End)); break;
            case "underline": Apply(t => HtmlEditing.Underline(t.Text, t.Start, t.End)); break;
            case "heading": Apply(t => HtmlEditing.WrapBlock(t.Text, t.Start, t.End, "h2")); break;
            case "quote": Apply(t => HtmlEditing.WrapBlock(t.Text, t.Start, t.End, "blockquote")); break;
            case "list": Apply(t => HtmlEditing.MakeList(t.Text, t.Start, t.End, numbered: false)); break;
            case "numbered": Apply(t => HtmlEditing.MakeList(t.Text, t.Start, t.End, numbered: true)); break;
            case "rule": Apply(t => HtmlEditing.WrapInline(t.Text, t.End, t.End, "\n<hr>\n", "")); break;
            case "link":
                var selection = (SourceBox.SelectionStart, SourceBox.SelectionEnd);
                var url = await PromptDialog.ShowAsync(this, "Link einfügen", "Adresse der Webseite oder E-Mail-Adresse:",
                    "https://beispiel.de", text => HtmlEditing.NormalizeUrl(text) is null ? "Das ist keine gültige Web- oder E-Mail-Adresse." : null);
                if (url is null) return;
                SourceBox.SelectionStart = selection.SelectionStart;
                SourceBox.SelectionEnd = selection.SelectionEnd;
                Apply(t => HtmlEditing.Link(t.Text, t.Start, t.End, url));
                break;
        }
    }

    readonly record struct Selection(string Text, int Start, int End);

    /// <summary>Runs a formatting step on the current selection and keeps the result selected.</summary>
    void Apply(Func<Selection, EditResult> edit)
    {
        var text = SourceBox.Text ?? "";
        var result = edit(new Selection(text, SourceBox.SelectionStart, SourceBox.SelectionEnd));
        SourceBox.Text = result.Text;
        SourceBox.SelectionStart = result.SelectionStart;
        SourceBox.SelectionEnd = result.SelectionEnd;
        SourceBox.Focus();
    }

    void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void UpdatePreview()
    {
        _previewTimer.Stop();
        if (!_vm.IsAdvanced) return;
        try
        {
            _preview.Text = Composer.ToMailHtml(_vm.HtmlSource);
        }
        catch (Exception)
        {
            // Half-typed markup must never break the editor; the preview just keeps its last state.
        }
    }

    /// <summary>Editor and preview side by side need some room.</summary>
    void EnsureWideEnough()
    {
        if (WindowState == WindowState.Normal && Width < 1040) Width = 1040;
    }
}
