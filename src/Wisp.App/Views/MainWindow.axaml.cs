using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
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
        _vm.ClearListSelectionRequested += () => MessageList.SelectedItems?.Clear();
        // Double-click on a mail starts the selection mode (not on the quick-action buttons in the row).
        MessageList.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
            _vm.IsSelectionMode = true;
        };

        MessageList.ContextRequested += OnMessageContextRequested;
        ReaderBody.ContextRequested += OnBodyContextRequested;
        // Strg+C / Strg+A for the text of the open mail. Tunnel, so the reader's own controls do not take them first.
        AddHandler(KeyDownEvent, OnCopyKeys, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // After deleting (archiving, moving …) the next mail is selected and opened. The keyboard stays in the
        // list, so Entf right away deletes that one too.
        _vm.SelectRequested += item =>
        {
            MessageList.SelectedItem = item;
            MessageList.ScrollIntoView(item);
            Dispatcher.UIThread.Post(() => MessageList.ContainerFromItem(item)?.Focus(), DispatcherPriority.Loaded);
        };

        // Two lists (pinned inbox + folders) act as one selection.
        PinnedList.SelectionChanged += (_, _) => OnSidebarListSelected(PinnedList, FolderList);
        FolderList.SelectionChanged += (_, _) => OnSidebarListSelected(FolderList, PinnedList);
        ShowSidebarSelection();
        ApplySidebarMode();
        // Choosing an account (or "Konto hinzufügen") closes the account list. Only afterwards: Click comes before
        // the button runs its command, and closing detaches the button, which loses its $parent[Window] command.
        AccountMenu.AddHandler(Button.ClickEvent, (_, _) => Dispatcher.UIThread.Post(() => AccountSwitcher.Flyout?.Hide()));
        SelectAddressOnDoubleClick(FromText);
        SelectAddressOnDoubleClick(RecipientsText);

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
            case nameof(MainViewModel.IsSelectionMode): ApplySelectionMode(); break;
        }
    }

    /// <summary>In selection mode a click toggles a mail instead of replacing the selection; leaving it deselects all.</summary>
    void ApplySelectionMode()
    {
        MessageList.SelectionMode = _vm.IsSelectionMode ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Multiple;
        if (!_vm.IsSelectionMode && MessageList.SelectedItems is { Count: > 0 } selected) selected.Clear();
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

    // ───────────────────────── Reader header ─────────────────────────

    /// <summary>
    /// Double-clicking an address selects all of it ("anna.m-schmidt@web.de"), not just the word between dots
    /// and dashes. Runs after the control's own word selection and replaces it; other words behave as usual.
    /// </summary>
    static void SelectAddressOnDoubleClick(SelectableTextBlock block) =>
        block.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount != 2 || block.Text is not { Length: > 0 } text) return;
            var index = Math.Clamp(block.TextLayout.HitTestPoint(e.GetPosition(block)).TextPosition, 0, text.Length - 1);
            if (!IsAddressChar(text[index])) return;
            var start = index;
            while (start > 0 && IsAddressChar(text[start - 1])) start--;
            var end = index;
            while (end < text.Length && IsAddressChar(text[end])) end++;
            while (end > start && text[end - 1] is '.' or '-') end--;
            if (text.AsSpan(start, end - start).IndexOf('@') < 0) return;
            block.SelectionStart = start;
            block.SelectionEnd = end;
        }, handledEventsToo: true);

    static bool IsAddressChar(char c) => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_' or '+' or '%' or '\'';

    // ───────────────────────── Reader body ─────────────────────────

    void RenderBody(RenderedMail? mail)
    {
        if (mail is null)
        {
            ReaderBody.Content = null;
            DateText.Text = "";
            return;
        }
        var date = mail.Date.ToLocalTime();
        DateText.Text = date.ToString("ddd, dd.MM.yyyy, HH:mm", German);
        ToolTip.SetTip(DateText, date.ToString("dddd, d. MMMM yyyy, HH:mm", German));
        ReaderBody.Content = mail.Html is not null ? HtmlView(mail) : TextView(mail);
    }

    Control TextView(RenderedMail mail)
    {
        var reading = _mail.Settings.Reading;
        var view = new TextMailView(mail, reading.ShowLinkList, reading.WarnSuspiciousLinks, this);
        view.LinkClicked += link => _ = OpenLinkAsync(link.Url, link.Suspicious);
        return view;
    }

    Control HtmlView(RenderedMail mail)
    {
        var view = new HtmlMailView(mail, _mail.Settings.Appearance.HtmlZoom);
        view.ZoomChanged += zoom =>
        {
            _mail.Settings.Appearance.HtmlZoom = zoom;
            try { _mail.SaveSettings(); } catch (IOException) { }
        };
        view.LinkClicked += url => _ = OpenLinkAsync(url, mail.Links.FirstOrDefault(l => l.Url == url)?.Suspicious ?? false);
        return view;
    }

    // ───────────────────────── Selecting & copying mail text ─────────────────────────

    IMailBody? Body => ReaderBody.Content as IMailBody;

    /// <summary>Strg+C: the text selected in the open mail. False if nothing is selected there.</summary>
    bool CopySelection()
    {
        if (Body?.SelectedText is not { Length: > 0 } text) return false;
        _ = CopyTextAsync(text);
        return true;
    }

    /// <summary>
    /// Strg+C copies the text selected in the open mail (a selection in the header copies itself);
    /// after a click into the mail Strg+A selects its text, otherwise it stays "all mails of the list".
    /// </summary>
    void OnCopyKeys(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox or ComboBox or NumericUpDown) return;
        switch (e.Key)
        {
            case Key.C when focused is not SelectableTextBlock { SelectedText.Length: > 0 }:
                e.Handled = CopySelection();
                break;
            case Key.A when Body is { } body && focused is Visual f && ReaderBody.IsVisualAncestorOf(f):
                body.SelectAll();
                e.Handled = true;
                break;
        }
    }

    async Task CopyTextAsync(string text, string? notice = null)
    {
        try
        {
            if (Clipboard is null) return;
            await Clipboard.SetTextAsync(text);
            if (notice is not null) _vm.ShowNotice(notice);
        }
        catch (Exception)
        {
            _vm.ShowNotice("Die Zwischenablage ist gerade von einem anderen Programm belegt. Bitte noch einmal versuchen.");
        }
    }

    /// <summary>Right-click in the open mail: copy the selection, select everything, copy the address of a link.</summary>
    void OnBodyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Body is not { } body || !_vm.ShowContextMenu) return;
        e.Handled = true;
        var selected = body.SelectedText;
        var items = new List<Control>
        {
            MenuEntry("Kopieren", "IconCopy", new RelayCommand(() => _ = CopyTextAsync(selected)), gesture: "Ctrl+C", enabled: selected.Length > 0),
            MenuEntry("Alles markieren", "IconSelectAll", new RelayCommand(body.SelectAll), gesture: "Ctrl+A"),
        };
        if (e.TryGetPosition((Control)body, out var point) && body.LinkAt(point) is { } link)
        {
            var isMail = link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            var address = isMail ? Uri.UnescapeDataString(link[7..].Split('?')[0]) : link;
            items.Add(new Separator());
            if (!isMail) items.Add(MenuEntry("Link öffnen", "IconExternal", new RelayCommand(() => _ = OpenLinkAsync(link, _vm.CurrentMail?.Links.FirstOrDefault(l => l.Url == link)?.Suspicious ?? false))));
            items.Add(MenuEntry(isMail ? "E-Mail-Adresse kopieren" : "Link-Adresse kopieren", "IconLink", new RelayCommand(() => _ = CopyTextAsync(address))));
        }
        new ContextMenu { ItemsSource = items }.Open(ReaderBody);
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

    /// <summary>One quiet hint per Wisp 2 version, with a way to the upgrade button ("Über Wisp").</summary>
    public void AnnounceWisp2(string version) =>
        _vm.ShowNotice($"Wisp {version} ist verfügbar – mit Einmalcodes, sortiertem Posteingang und optionaler lokaler KI.", "Ansehen", async () =>
        {
            var vm = new SettingsViewModel(_mail, _vault, this) { SectionIndex = 6 };
            await new SettingsWindow(vm).ShowDialog(this);
            _vm.ReloadAll();
        }, TimeSpan.FromSeconds(15));

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
            var account = _vm.ActiveAccount?.AccountId ?? _mail.GetAccounts().FirstOrDefault()?.Id;
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
