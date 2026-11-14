using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wisp.App.ViewModels;

namespace Wisp.App.Views;

/// <summary>
/// Keyboard (and mouse back/forward) navigation of the main window. Every key here is listed in
/// <see cref="ShortcutsWindow"/> (F1); keep the two in step.
/// Strg combinations work everywhere, the single keys only while no text field has the keyboard.
/// Strg+N/R/Umschalt+R/L/U and F5 stay as KeyBindings in MainWindow.axaml. Esc is handled here: a KeyBinding
/// would run before this handler and close the mail even while Esc is meant to empty the search box.
/// </summary>
public partial class MainWindow
{
    void SetUpKeyboard()
    {
        // Tunnel: the lists, the reader and the text boxes would otherwise use the keys first.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSideButton, RoutingStrategies.Tunnel, handledEventsToo: true);
        MessageList.Focusable = true; // so the keyboard can land in the list while no mail is open

        // The two sidebar lists act as one for ↑/↓: down from the last pinned entry into the folders, up back again.
        PinnedList.KeyDown += (_, e) => e.Handled = e.Key == Key.Down && PinnedList.SelectedIndex == PinnedList.ItemCount - 1 && MoveInto(FolderList, 0);
        FolderList.KeyDown += (_, e) => e.Handled = e.Key == Key.Up && FolderList.SelectedIndex == 0 && MoveInto(PinnedList, PinnedList.ItemCount - 1);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        var inText = focused is TextBox or ComboBox or Slider or NumericUpDown or MenuItem;
        var mods = e.KeyModifiers;

        // The command field owns the keyboard while it is open; Strg+K closes it again.
        if (_vm.IsPaletteOpen)
        {
            if (e.Key == Key.K && mods == KeyModifiers.Control) _vm.ClosePalette();
            else if (!OnPaletteKey(e.Key, mods)) return;
            e.Handled = true;
            return;
        }
        // Send options above the send button: Esc folds them back in.
        if (_vm.IsSendMenuOpen && e.Key == Key.Escape)
        {
            _vm.CloseSendMenu();
            e.Handled = true;
            return;
        }
        if (focused == QuickReplyBox && OnQuickReplyKey(e.Key, mods))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F1 when mods == KeyModifiers.None:
                e.Handled = true;
                _ = new ShortcutsWindow().ShowDialog(this);
                return;
            case Key.F3 when mods == KeyModifiers.None:
                e.Handled = FocusSearch();
                return;
        }

        if (mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = OnControlKey(e.Key, mods.HasFlag(KeyModifiers.Shift), inText);
            return;
        }
        if (focused == SearchBox)
        {
            e.Handled = OnSearchKey(e.Key, mods);
            return;
        }
        if (e.Key == Key.Escape && mods == KeyModifiers.None)
        {
            // Closes the open mail (and ends the selection mode), wherever the keyboard is.
            _vm.CloseMailCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (inText) return;

        var inSidebar = focused is Visual v && v.FindAncestorOfType<ListBox>(includeSelf: true) is { } list && (list == PinnedList || list == FolderList);
        var onButton = focused is Button or ToggleButton;
        e.Handled = mods switch
        {
            KeyModifiers.None => OnPlainKey(e.Key, inSidebar, onButton),
            KeyModifiers.Shift => OnShiftKey(e.Key, inSidebar, onButton),
            _ => false,
        };
    }

    bool OnControlKey(Key key, bool shift, bool inText)
    {
        switch (key)
        {
            case Key.F: return FocusSearch();
            case Key.K when !shift:
                _vm.OpenPalette();
                return true;
            case Key.Z when !shift && !inText: return _vm.Undo();
            case Key.S when !shift && !inText:
                _vm.ExportCommand.Execute(null);
                return true;
            // Text selected in the open mail. A selection in the header (subject, addresses) copies itself.
            case Key.C when !shift && !inText && FocusManager?.GetFocusedElement() is not SelectableTextBlock { SelectedText.Length: > 0 }:
                return CopySelection();
            // After a click into the mail Strg+A means its text; otherwise it stays "all mails of the list".
            case Key.A when !shift && !inText && Body is { } body && FocusManager?.GetFocusedElement() is Visual f && ReaderBody.IsVisualAncestorOf(f):
                body.SelectAll();
                return true;
            case Key.Tab:
                if (!_vm.CycleCategory(shift ? -1 : 1)) return false;
                Dispatcher.UIThread.Post(FocusMessageList, DispatcherPriority.Loaded);
                return true;
            case >= Key.D1 and <= Key.D9 when !shift: return GoToFolder(key - Key.D1);
            case >= Key.NumPad1 and <= Key.NumPad9 when !shift: return GoToFolder(key - Key.NumPad1);
            case Key.OemComma when !shift:
                _vm.OpenSettingsCommand.Execute(null);
                return true;
        }
        // In text fields Strg+arrows and Strg+D keep their usual meaning.
        if (inText || shift) return false;
        switch (key)
        {
            case Key.Down: return MoveMail(1);
            case Key.Up: return MoveMail(-1);
            case Key.D:
                _vm.DeleteCommand.Execute(null);
                return true;
        }
        return false;
    }

    /// <summary>Search box: Esc empties it (a second Esc goes back to the list), ↓ or Enter jumps into the results.</summary>
    bool OnSearchKey(Key key, KeyModifiers mods)
    {
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.Escape when !string.IsNullOrEmpty(_vm.SearchText):
                _vm.SearchText = "";
                return true;
            case Key.Escape:
                FocusMessageList();
                return true;
            case Key.Down or Key.Enter:
                if (MessageList.SelectedIndex < 0 && _vm.Messages.Count > 0) return MoveMail(1);
                FocusMessageList();
                return true;
        }
        return false;
    }

    /// <summary>Quick reply field: Strg+Enter sends, Esc goes back to the list (the text stays with its mail).</summary>
    bool OnQuickReplyKey(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Enter when mods == KeyModifiers.Control:
                _vm.SendQuickReplyCommand.Execute(null);
                return true;
            case Key.Escape when mods == KeyModifiers.None:
                FocusMessageList();
                return true;
        }
        return false;
    }

    bool OnPlainKey(Key key, bool inSidebar, bool onButton)
    {
        switch (key)
        {
            // Always handled, even at the end of the row: otherwise Avalonia moves the focus on its own,
            // e.g. into the search box, which then keeps every further arrow key.
            case Key.Left or Key.Right:
                Step(key == Key.Left);
                return true;
            case Key.Enter when inSidebar:
                FocusMessageList();
                return true;
            case Key.Enter when !onButton && _vm.QuickDrafts && _vm.CurrentMessageId is not null:
                _vm.EditDraftCommand.Execute(null);
                return true;
            // Enter answers: straight into the quick reply under the open mail.
            case Key.Enter when !onButton && _vm.CanQuickReply && QuickReplyBox.IsEffectivelyVisible:
                QuickReplyBox.Focus();
                QuickReplyBox.CaretIndex = QuickReplyBox.Text?.Length ?? 0;
                return true;
            case Key.Space when !onButton: return PageReader(up: false);
            case >= Key.D1 and <= Key.D5: return SelectTab(key - Key.D1);
            case >= Key.NumPad1 and <= Key.NumPad5: return SelectTab(key - Key.NumPad1);
        }
        // Keys that act on the open mail: not while the keyboard is in the sidebar, where they would surprise.
        if (inSidebar) return false;
        switch (key)
        {
            case Key.Delete: Run(_vm.DeleteCommand); return true;
            case Key.A when _vm.CanArchive: Run(_vm.ArchiveCommand); return true;
            case Key.S: Run(_vm.ToggleFlagCommand); return true;
            case Key.U: Run(_vm.ToggleReadCommand); return true;
            case Key.J when _vm.QuickJunk || _vm.CanMarkSpam: Run(_vm.ToggleSpamCommand); return true;
            case Key.R: Run(_vm.ReplyCommand); return true;
            case Key.W: Run(_vm.ForwardCommand); return true;
            case Key.N: Run(_vm.ComposeCommand); return true;
            case Key.Z when _vm.CanSummarize: Run(_vm.SummarizeCommand); return true; // AIEdition
        }
        return false;
    }

    bool OnShiftKey(Key key, bool inSidebar, bool onButton) => key switch
    {
        Key.Space when !onButton => PageReader(up: true),
        Key.R when !inSidebar => Run(_vm.ReplyAllCommand),
        _ => false,
    };

    static bool Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
        return true;
    }

    bool SelectTab(int index)
    {
        if (!_vm.SelectCategoryAt(index)) return false;
        Dispatcher.UIThread.Post(FocusMessageList, DispatcherPriority.Loaded);
        return true;
    }

    bool GoToFolder(int index)
    {
        if (!_vm.GoToSidebarEntry(index)) return false;
        Dispatcher.UIThread.Post(FocusMessageList, DispatcherPriority.Loaded);
        return true;
    }

    bool FocusSearch()
    {
        if (!SearchBox.IsEffectivelyVisible) return false;
        SearchBox.Focus();
        SearchBox.SelectAll();
        return true;
    }

    /// <summary>Next or previous mail in the list (Strg+↓/↑, works while reading); opens it.</summary>
    bool MoveMail(int delta)
    {
        var count = _vm.Messages.Count;
        if (count == 0) return true;
        var current = MessageList.SelectedIndex;
        var index = current < 0 ? (delta > 0 ? 0 : count - 1) : Math.Clamp(current + delta, 0, count - 1);
        if (index == current) return true;
        return SelectMail(index);
    }

    /// <summary>Selects (and so opens) exactly this mail, scrolls it into view and puts the keyboard on it.</summary>
    bool SelectMail(int index)
    {
        MessageList.SelectedIndex = index;
        MessageList.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => MessageList.ContainerFromIndex(index)?.Focus(), DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>
    /// Space reads on: one screen further down in the open mail, at its end the next unread mail (like Thunderbird);
    /// with no mail open, the first unread one. Umschalt+Space goes back up.
    /// </summary>
    bool PageReader(bool up)
    {
        var scroll = ReaderBody.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible);
        if (scroll is null || ReaderBody.Content is null || _vm.CurrentMessageId is null) return up || NextUnread();
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = scroll.Offset.Y;
        if (!up && y >= max - 1) return NextUnread();
        var step = Math.Max(40, scroll.Viewport.Height - 60); // keep a few lines for orientation
        scroll.Offset = scroll.Offset.WithY(Math.Clamp(y + (up ? -step : step), 0, max));
        return true;
    }

    /// <summary>The next unread mail below the selected one (else from the top); without any, simply the next mail.</summary>
    bool NextUnread()
    {
        var messages = _vm.Messages;
        var current = MessageList.SelectedIndex;
        for (var i = current + 1; i < messages.Count; i++)
            if (messages[i].IsUnread) return SelectMail(i);
        for (var i = 0; i < current; i++)
            if (messages[i].IsUnread) return SelectMail(i);
        return MoveMail(1);
    }

    // ───────────────────────── Left / right ─────────────────────────

    void OnSideButton(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
        e.Handled = Step(kind == PointerUpdateKind.XButton1Pressed);
    }

    /// <summary>One step left or right; the keyboard follows: into the sidebar, back to the mail list.</summary>
    bool Step(bool left)
    {
        switch (left ? _vm.StepLeft() : _vm.StepRight())
        {
            case MainViewModel.SideStep.IntoSidebar:
                Dispatcher.UIThread.Post(FocusSidebar, DispatcherPriority.Loaded);
                return true;
            case MainViewModel.SideStep.OutOfSidebar or MainViewModel.SideStep.Tab:
                Dispatcher.UIThread.Post(FocusMessageList, DispatcherPriority.Loaded);
                return true;
            default:
                return false;
        }
    }

    // Focus only, never select: a directional focus would make the list select (and open) the item.
    void FocusSidebar() => FocusSelected(PinnedList.SelectedItem is not null ? PinnedList : FolderList);
    void FocusMessageList() => FocusSelected(MessageList);

    // The selected row, else the list itself (MessageList is made focusable for that; ↓ then picks the first mail).
    static void FocusSelected(ListBox list)
    {
        if (list.SelectedItem is { } item && list.ContainerFromItem(item)?.Focus() == true) return;
        list.Focus();
    }

    static bool MoveInto(ListBox list, int index)
    {
        if (index < 0 || index >= list.ItemCount) return false;
        list.SelectedIndex = index;
        list.ContainerFromIndex(index)?.Focus();
        return true;
    }
}
