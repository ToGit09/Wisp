using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TheArtOfDev.HtmlRenderer.Avalonia;
using Wisp.App.ViewModels;
using Wisp.Core;
using Wisp.Core.Mail;
using Wisp.Core.Rendering;
using Wisp.Core.Security;

namespace Wisp.App.Views;

public partial class MainWindow : Window, IDialogs
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    readonly MailService _mail;
    readonly Vault _vault;
    readonly MainViewModel _vm;

    // Needed by the XAML previewer only.
    public MainWindow() : this(null!, null!) { }

    public MainWindow(MailService mail, Vault vault)
    {
        InitializeComponent();
        _mail = mail;
        _vault = vault;
        if (mail is null) { _vm = null!; return; }

        Width = mail.Settings.WindowWidth;
        Height = mail.Settings.WindowHeight;
        _vm = new MainViewModel(mail, this);
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;

        MessageList.SelectionChanged += (_, _) =>
            _vm.SetSelection(MessageList.SelectedItems?.OfType<MessageItem>().Select(m => m.Id).ToList() ?? []);
        MessageList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) _vm.DeleteCommand.Execute(null);
        };

        Opened += async (_, _) =>
        {
            if (!_vm.HasAccounts) await OpenAccountSetupAsync();
        };
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                mail.Settings.WindowWidth = Width;
                mail.Settings.WindowHeight = Height;
                try { mail.SaveSettings(); } catch (IOException) { }
            }
        };
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentMail)) RenderBody(_vm.CurrentMail);
    }

    // ───────────────────────── Reader body ─────────────────────────

    void RenderBody(RenderedMail? mail)
    {
        if (mail is null)
        {
            ReaderBody.Content = null;
            DateText.Text = "";
            return;
        }
        DateText.Text = mail.Date.ToLocalTime().ToString("dddd, d. MMMM yyyy, HH:mm", German);
        ReaderBody.Content = mail.Html is not null ? HtmlView(mail) : TextView(mail);
    }

    Control TextView(RenderedMail mail)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24, 16, 24, 24), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        var accent = this.Brush("WispAccent");
        var muted = this.Brush("WispMuted");

        foreach (var block in mail.Blocks)
        {
            if (block.Kind == BlockKind.Separator)
            {
                panel.Children.Add(new Border { Height = 1, Background = this.Brush("WispBorder"), Margin = new Thickness(0, 6) });
                continue;
            }
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    text.FontWeight = FontWeight.SemiBold;
                    text.FontSize = (double)(this.FindResource("WispFontSize") ?? 14.0) + 3;
                    break;
                case BlockKind.Quote:
                    text.Foreground = muted;
                    break;
                case BlockKind.Preformatted:
                    text.FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
                    break;
            }
            if (block.Kind == BlockKind.ListItem) text.Inlines!.Add(new Run("•  "));
            foreach (var span in block.Spans)
            {
                var run = new Run(span.Text)
                {
                    FontWeight = span.Bold ? FontWeight.SemiBold : FontWeight.Normal,
                    FontStyle = span.Italic ? FontStyle.Italic : FontStyle.Normal,
                };
                text.Inlines!.Add(run);
                if (span.LinkIndex >= 0)
                {
                    run.Foreground = accent;
                    run.TextDecorations = TextDecorations.Underline;
                    var link = mail.Links[span.LinkIndex];
                    if (link.Url != span.Text && link.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        text.Inlines.Add(new Run($" [{link.Index + 1}]") { Foreground = muted, FontSize = 11 });
                }
            }

            Control element = text;
            if (block.Kind == BlockKind.Quote)
            {
                element = new Border
                {
                    BorderBrush = this.Brush("WispBorder"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Child = text,
                };
            }
            panel.Children.Add(element);
        }

        if (mail.Blocks.Count == 0)
            panel.Children.Add(new TextBlock { Text = "(Diese Mail hat keinen Textinhalt.)", Foreground = muted });

        var links = mail.Links.Where(l => l.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Links in dieser Mail", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
            foreach (var link in links)
            {
                var row = new StackPanel { Spacing = 2 };
                var button = new Button
                {
                    Classes = { "link" },
                    Content = new TextBlock { Text = $"[{link.Index + 1}]  {link.Url}", TextWrapping = TextWrapping.Wrap },
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                button.Click += async (_, _) => await OpenLinkAsync(link.Url, link.Suspicious);
                row.Children.Add(button);
                if (link.Suspicious)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = $"⚠ Vorsicht: Der Linktext zeigt eine andere Adresse als das tatsächliche Ziel ({link.Host}).",
                        Foreground = this.Brush("WispDanger"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                    });
                }
                panel.Children.Add(row);
            }
        }

        return new ScrollViewer { Content = panel };
    }

    Control HtmlView(RenderedMail mail)
    {
        var view = new HtmlMailView(mail.Html!,
            "body { font-family: Segoe UI, Ubuntu, Cantarell, sans-serif; font-size: 10.5pt; color: #1d1f24; } a { color: #3557d4; }",
            _mail.Settings.Appearance.MailZoom);
        view.ZoomChanged += zoom =>
        {
            _mail.Settings.Appearance.MailZoom = zoom;
            try { _mail.SaveSettings(); } catch (IOException) { }
        };
        var html = view.Html;

        html.ImageLoad += (_, e) =>
        {
            var args = e.Event;
            var src = args.Src ?? "";
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return; // decoded locally by the renderer
            args.Handled = true;

            if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                if (mail.InlineImages.TryGetValue(src[4..].Trim('<', '>'), out var bytes) && TryBitmap(bytes) is { } inline) args.Callback(inline);
                else args.Callback(Blank);
                return;
            }
            if (!mail.RemoteImagesAllowed || !HtmlSanitizer.IsRemote(src))
            {
                args.Callback(Blank); // blocked: nothing is contacted, and no "broken image" icon either
                return;
            }
            _ = Task.Run(async () =>
            {
                var data = await RemoteImageLoader.LoadAsync(src, CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (data is not null && TryBitmap(data) is { } bitmap) args.Callback(bitmap);
                    else args.Callback(Blank);
                });
            });
        };
        html.StylesheetLoad += (_, e) => e.Event.SetStyleSheet = ""; // never fetch external CSS
        html.LinkClicked += async (_, e) =>
        {
            e.Event.Handled = true;
            var url = e.Event.Link ?? "";
            var known = mail.Links.FirstOrDefault(l => l.Url == url);
            await OpenLinkAsync(url, known?.Suspicious ?? false);
        };
        return view;
    }

    static Bitmap? TryBitmap(byte[] data)
    {
        try { return new Bitmap(new MemoryStream(data)); }
        catch { return null; } // corrupt or unsupported (e.g. SVG)
    }

    /// <summary>A fresh transparent 1×1 image; the renderer may dispose what it is given.</summary>
    static Bitmap Blank
    {
        get
        {
            var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var frame = bitmap.Lock())
                System.Runtime.InteropServices.Marshal.WriteInt32(frame.Address, 0);
            return bitmap;
        }
    }

    /// <summary>
    /// Called when drawing the reader failed (see App). Falls back to the plain text view of the same mail
    /// instead of letting one odd mail take the whole app down.
    /// </summary>
    public void RecoverFromRenderError()
    {
        if (ReaderBody.Content is not HtmlMailView || _vm.CurrentMail is not { } mail) return;
        ReaderBody.Content = TextView(mail);
        _vm.ShowNotice("Diese Mail ließ sich nicht im Originalformat darstellen und wird als Text angezeigt.");
    }

    // ───────────────────────── IDialogs ─────────────────────────

    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        ConfirmDialog.ShowAsync(this, title, message, confirm, danger);

    public void OpenCompose(Draft draft)
    {
        var window = new ComposeWindow(new ComposeViewModel(_mail, draft));
        window.Show(this);
    }

    public async Task OpenSettingsAsync() => await new SettingsWindow(new SettingsViewModel(_mail, _vault, this)).ShowDialog(this);

    public async Task OpenAccountSetupAsync()
    {
        await new AccountSetupWindow(new AccountSetupViewModel(_mail)).ShowDialog(this);
        _vm.ReloadAll();
    }

    public async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Anhang speichern",
            SuggestedFileName = suggestedName,
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task OpenLinkAsync(string url, bool suspicious)
    {
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = Uri.UnescapeDataString(url[7..].Split('?')[0]);
            var account = _vm.SelectedSidebarItem?.AccountId ?? _mail.GetAccounts().FirstOrDefault()?.Id;
            if (account is long id) OpenCompose(new Draft { AccountId = id, To = address });
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;

        if (suspicious && !await ConfirmAsync("Verdächtiger Link",
                $"Dieser Link führt nicht dorthin, wo sein Text vermuten lässt.\n\nTatsächliches Ziel:\n{uri.Host}\n\n{url}\n\nNur öffnen, wenn Sie dem Absender vertrauen.",
                "Trotzdem öffnen", danger: true))
            return;

        await Launcher.LaunchUriAsync(uri);
    }
}
