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
        SetUpKeyboard(); // MainWindow.Keys.cs
        SetUpMotion(); // MainWindow.Motion.cs
        _vm.QuickReplySent += () => Dispatcher.UIThread.Post(FocusMessageList, DispatcherPriority.Loaded);
        _vm.ShortcutsRequested += () => _ = new ShortcutsWindow().ShowDialog(this);
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
            case nameof(MainViewModel.CurrentMessageId): OnCurrentMessageChanged(); break;
            case nameof(MainViewModel.Notice) or nameof(MainViewModel.NoticeActionLabel): UpdateToast(); break;
            case nameof(MainViewModel.IsPaletteOpen): UpdatePalette(); break;
            case nameof(MainViewModel.IsSendMenuOpen) or nameof(MainViewModel.IsSendLaterOpen): UpdateSendMenu(); break;
            case nameof(MainViewModel.ShowSummary) when _vm.ShowSummary: _ = Wisp.App.Theme.Motion.In(SummaryCard, dy: 4, ms: 200); break;
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
        // Grouped by what one does with a mail: answer – mark – put away. Hidden instead of greyed out where an
        // entry makes no sense here; the letters on the right are the real shortcuts of the list.
        var items = new List<Control>
        {
            MenuEntry("Antworten", "IconReply", _vm.ReplyCommand, keys: "R", enabled: single),
            MenuEntry("Allen antworten", "IconReplyAll", _vm.ReplyAllCommand, keys: "⇧ R", enabled: single),
            MenuEntry("Weiterleiten", "IconForward", _vm.ForwardCommand, keys: "W", enabled: single),
            new Separator(),
            MenuEntry(clicked.IsUnread ? "Als gelesen markieren" : "Als ungelesen markieren", "IconMail", _vm.ToggleReadCommand, keys: "U"),
            MenuEntry(clicked.IsFlagged ? "Markierung entfernen" : "Markieren", "IconStar", _vm.ToggleFlagCommand, keys: "S"),
        };
        if (_vm.CanSummarize && single) items.Add(MenuEntry("Zusammenfassen", "IconSpark", _vm.SummarizeCommand, keys: "Z"));
        items.Add(new Separator());
        items.Add(MoveMenu());
        if (_vm.CanArchive) items.Add(MenuEntry("Archivieren", "IconArchive", _vm.ArchiveCommand, keys: "A"));
        if (_vm.QuickJunk) items.Add(MenuEntry("Kein Spam", "IconInbox", _vm.NotSpamCommand, keys: "J"));
        else if (_vm.CanMarkSpam) items.Add(MenuEntry("Als Spam markieren", "IconJunk", _vm.MarkSpamCommand, keys: "J"));
        items.Add(MenuEntry(single ? "Exportieren …" : "Alle exportieren …", "IconExport", _vm.ExportCommand, keys: "Strg S"));
        items.Add(MenuEntry("Löschen", "IconTrash", _vm.DeleteCommand, keys: "Entf", danger: true));
        if (_vm.ShowSortMenu)
        {
            items.Add(new Separator());
            items.Add(SortMenu(enabled: single));
        }
        new ContextMenu { ItemsSource = items }.Open(MessageList);
    }

    /// <summary>Wisp 2: "Absender immer einordnen als …" – a rule for every mail of this sender.</summary>
    MenuItem SortMenu(bool enabled)
    {
        MenuItem Entry(string header, Core.Smart.MailCategory? category) => new()
        {
            Header = header,
            Command = _vm.SortSenderCommand,
            CommandParameter = category is null ? "none" : category.ToString(),
        };
        return new MenuItem
        {
            Header = "Absender immer einordnen als",
            Icon = MenuIcon("IconFolder"),
            IsVisible = _vm.ShowSortMenu,
            IsEnabled = enabled,
            ItemsSource = new[]
            {
                Entry("Persönlich", Core.Smart.MailCategory.Personal),
                Entry("Rechnungen", Core.Smart.MailCategory.Invoice),
                Entry("Werbung", Core.Smart.MailCategory.Promotion),
                Entry("Spamverdacht", Core.Smart.MailCategory.SuspectedSpam),
                Entry("Keine Regel (automatisch)", null),
            },
        };
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

    /// <param name="keys">Shortcut shown on the right, in German ("Entf", "⇧ R").</param>
    /// <param name="danger">Deleting: the row lights up red instead of in the accent colour.</param>
    MenuItem MenuEntry(string header, string icon, System.Windows.Input.ICommand command, string? keys = null, bool enabled = true, bool danger = false)
    {
        object content = header;
        if (keys is not null)
        {
            var hint = new TextBlock { Text = keys, Classes = { "menukey" }, Margin = new Thickness(24, 0, 0, 0) };
            DockPanel.SetDock(hint, Dock.Right);
            content = new DockPanel { Children = { hint, new TextBlock { Text = header } } };
        }
        var item = new MenuItem { Header = content, Icon = MenuIcon(icon), Command = command, IsEnabled = enabled };
        if (danger) item.Classes.Add("danger");
        return item;
    }

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
            return;
        }
        ReaderBody.Content = mail.Html is not null ? HtmlView(mail) : TextView(mail);
        var newMail = _vm.CurrentMessageId != _renderedId;
        _renderedId = _vm.CurrentMessageId;
        RevealBody(newMail);
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
            MenuEntry("Kopieren", "IconCopy", new RelayCommand(() => _ = CopyTextAsync(selected)), keys: "Strg C", enabled: selected.Length > 0),
            MenuEntry("Alles markieren", "IconSelectAll", new RelayCommand(body.SelectAll), keys: "Strg A"),
        };
        if (e.TryGetPosition((Control)body, out var point) && body.LinkAt(point) is { } link)
        {
            var isMail = link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            var address = isMail ? Uri.UnescapeDataString(link[7..].Split('?')[0]) : link;
            items.Add(new Separator());
            if (!isMail) items.Add(MenuEntry("Link öffnen", "IconExternal", new RelayCommand(() => _ = _vm.OpenLinkUrl(link, _vm.CurrentMail?.Links.FirstOrDefault(l => l.Url == link)?.Suspicious ?? false))));
            items.Add(MenuEntry(isMail ? "E-Mail-Adresse kopieren" : "Link-Adresse kopieren", "IconLink", new RelayCommand(() => _ = CopyTextAsync(address))));
        }
        items.Add(new Separator());
        items.Add(MenuEntry("Mail exportieren …", "IconExport", _vm.ExportCommand, keys: "Strg S"));
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

    public void OpenCompose(Draft draft, bool restored = false, string? typed = null)
    {
        var vm = new ComposeViewModel(_mail, draft, restored, typed);
        vm.Sent += _vm.NotifySent;
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

    public async Task<(Stream Stream, string Name)?> SaveExportAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Mail exportieren",
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            DefaultExtension = "eml",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("E-Mail im Original (.eml)") { Patterns = ["*.eml"], MimeTypes = ["message/rfc822"] },
                new FilePickerFileType("Webseite zum Ansehen und Drucken (.html)") { Patterns = ["*.html"], MimeTypes = ["text/html"] },
                new FilePickerFileType("Nur Text (.txt)") { Patterns = ["*.txt"], MimeTypes = ["text/plain"] },
            ],
        });
        return file is null ? null : (await file.OpenWriteAsync(), file.Name);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
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

    public Task CopySecretAsync(string text) => SecretClipboard.SetAsync(Clipboard, text);

    public Task ClearClipboardIfAsync(string text) => SecretClipboard.ClearIfAsync(Clipboard, text);
}
