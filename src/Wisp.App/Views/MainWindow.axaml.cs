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
using Avalonia.VisualTree;
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

        MessageList.ContextRequested += OnMessageContextRequested;

        // Two lists (pinned inboxes + folders) act as one selection.
        PinnedList.SelectionChanged += (_, _) => OnSidebarListSelected(PinnedList, FolderList);
        FolderList.SelectionChanged += (_, _) => OnSidebarListSelected(FolderList, PinnedList);
        ShowSidebarSelection();
        ApplySidebarMode();

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
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentMail): RenderBody(_vm.CurrentMail); break;
            case nameof(MainViewModel.SelectedSidebarItem): ShowSidebarSelection(); break;
            case nameof(MainViewModel.SidebarAlwaysOpen): ApplySidebarMode(); break;
        }
    }

    // ───────────────────────── Right-click menu ─────────────────────────

    void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_vm.ShowContextMenu) return;

        // Right-clicking a mail that is not selected selects it (like in Explorer).
        var clicked = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MessageItem;
        if (clicked is null) return;
        if (MessageList.SelectedItems?.Contains(clicked) != true) MessageList.SelectedItem = clicked;

        var single = MessageList.SelectedItems?.Count == 1;
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                MenuEntry("Antworten", "IconReply", _vm.ReplyCommand, gesture: "Ctrl+R", enabled: single),
                MenuEntry("Allen antworten", "IconReplyAll", _vm.ReplyAllCommand, gesture: "Ctrl+Shift+R", enabled: single),
                MenuEntry("Weiterleiten", "IconForward", _vm.ForwardCommand, gesture: "Ctrl+L", enabled: single),
                new Separator(),
                MenuEntry(clicked.IsUnread ? "Als gelesen markieren" : "Als ungelesen markieren", "IconMail", _vm.ToggleReadCommand, gesture: "Ctrl+U"),
                MenuEntry(clicked.IsFlagged ? "Markierung entfernen" : "Markieren", "IconStar", _vm.ToggleFlagCommand),
                new Separator(),
                MoveMenu(),
                _vm.QuickJunk
                    ? MenuEntry("Kein Spam", "IconInbox", _vm.NotSpamCommand)
                    : MenuEntry("Als Spam markieren", "IconJunk", _vm.MarkSpamCommand, enabled: _vm.CanMarkSpam),
                MenuEntry("Archivieren", "IconArchive", _vm.ArchiveCommand, enabled: _vm.CanArchive),
                MenuEntry("Löschen", "IconTrash", _vm.DeleteCommand, gesture: "Delete"),
            },
        };
        menu.Open(MessageList);
    }

    MenuItem MoveMenu()
    {
        var item = new MenuItem { Header = "Verschieben", Icon = MenuIcon("IconMove") };
        item.ItemsSource = _vm.MoveTargets
            .Select(target => new MenuItem
            {
                Header = target.Name,
                Icon = new Theme.WispIcon { Data = target.Icon },
                Command = _vm.MoveToCommand,
                CommandParameter = target,
            })
            .ToList();
        item.IsEnabled = _vm.MoveTargets.Count > 0;
        return item;
    }

    MenuItem MenuEntry(string header, string icon, System.Windows.Input.ICommand command, string? gesture = null, bool enabled = true) => new()
    {
        Header = header,
        Icon = MenuIcon(icon),
        Command = command,
        InputGesture = gesture is null ? null : KeyGesture.Parse(gesture),
        IsEnabled = enabled,
    };

    Theme.WispIcon MenuIcon(string key) => new() { Data = this.FindResource(key) as Geometry };

    // ───────────────────────── Sidebar ─────────────────────────

    bool _syncingSidebar;

    void OnSidebarListSelected(ListBox source, ListBox other)
    {
        if (_syncingSidebar || source.SelectedItem is not SidebarItem item) return;
        _syncingSidebar = true;
        other.SelectedItem = null;
        _syncingSidebar = false;
        _vm.SelectedSidebarItem = item;
    }

    void ShowSidebarSelection()
    {
        _syncingSidebar = true;
        var selected = _vm.SelectedSidebarItem;
        PinnedList.SelectedItem = selected is not null && _vm.Pinned.Contains(selected) ? selected : null;
        FolderList.SelectedItem = selected is not null && _vm.Sidebar.Contains(selected) ? selected : null;
        _syncingSidebar = false;
    }

    /// <summary>Always-open sidebar takes its own column; the auto-collapsing one overlays the list.</summary>
    void ApplySidebarMode() =>
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_vm.SidebarAlwaysOpen ? 240 : 64);

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
        if (links.Count > 0 && _mail.Settings.Reading.ShowLinkList)
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
                if (link.Suspicious && _mail.Settings.Reading.WarnSuspiciousLinks)
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
            _mail.Settings.Appearance.HtmlZoom);
        view.ZoomChanged += zoom =>
        {
            _mail.Settings.Appearance.HtmlZoom = zoom;
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

    public void OpenCompose(Draft draft) => OpenCompose(draft, restored: false);

    void OpenCompose(Draft draft, bool restored)
    {
        var vm = new ComposeViewModel(_mail, draft, restored);
        vm.Sent += (result, sent) =>
        {
            if (result.OutboxId is not long id || result.UndoWindow <= TimeSpan.Zero)
            {
                _vm.ShowNotice("Die Mail wird gesendet.");
                return;
            }
            _vm.ShowNotice($"Die Mail wird in {result.UndoWindow.TotalSeconds:0} Sekunden gesendet.", "Rückgängig", () =>
            {
                if (_mail.TryUndoSend(id, sent)) OpenCompose(sent, restored: true);
                else _vm.ShowNotice("Zu spät – die Mail ist bereits unterwegs.");
            }, result.UndoWindow);
        };
        new ComposeWindow(vm).Show(this);
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

        var reading = _mail.Settings.Reading;
        if (suspicious && reading.WarnSuspiciousLinks)
        {
            if (!await ConfirmAsync("Verdächtiger Link",
                    $"Dieser Link führt nicht dorthin, wo sein Text vermuten lässt.\n\nTatsächliches Ziel:\n{uri.Host}\n\n{url}\n\nNur öffnen, wenn Sie dem Absender vertrauen.",
                    "Trotzdem öffnen", danger: true))
                return;
        }
        else if (reading.ConfirmLinks == Core.Settings.LinkConfirmation.Always &&
                 !await ConfirmAsync("Link öffnen?", $"Ziel: {uri.Host}\n\n{url}", "Öffnen"))
        {
            return;
        }

        await Launcher.LaunchUriAsync(uri);
    }
}
