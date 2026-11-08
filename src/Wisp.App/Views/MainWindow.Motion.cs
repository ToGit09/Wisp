using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wisp.App.Theme;
using Wisp.App.ViewModels;

namespace Wisp.App.Views;

/// <summary>
/// Everything that moves in the main window: the reader fading in a new mail, the toast, the command field
/// and the gliding tab pill. Rules: appear with ease-out, leave with ease-in, â‰¤ 240 ms, â‰¤ 12 px; never show a
/// stale or half state (old mail under a new header, a loading text for one frame, a text vanishing mid-fade).
/// </summary>
public partial class MainWindow
{
    void SetUpMotion()
    {
        Toast.PointerEntered += (_, _) => _vm.HoldNotice(true);
        Toast.PointerExited += (_, _) => _vm.HoldNotice(false);

        PaletteBackdrop.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _vm.ClosePalette();
        };
        // A click runs the entry (selection alone, e.g. by the mouse wheel, does not).
        PaletteList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PaletteEntry entry)
                _vm.RunPalette(entry);
        }, handledEventsToo: true);
        PaletteList.Focusable = false;

        TabItems.LayoutUpdated += (_, _) => PlaceTabPill();
        _vm.CategoryTabs.CollectionChanged += (_, _) => _pillPlaced = false;

        QuickReplyBox.SizeChanged += (_, _) =>
        {
            if (_toastShown) PlaceToast();
        };

        // Right-click (or holding a finger) on the send button opens the send options; also when the button is
        // greyed out (nothing typed yet), the options are then shown greyed out too.
        QuickSendHost.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (_vm.IsSendMenuOpen) _vm.CloseSendMenu();
            else _vm.OpenSendMenu();
        };
        // A click anywhere else folds the capsules back in â€“ and does nothing else, like any menu.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!_vm.IsSendMenuOpen || e.Source is not Visual source) return;
            if (SendMenu.IsVisualAncestorOf(source) || SendTimesMenu.IsVisualAncestorOf(source)) return;
            e.Handled = true;
            _vm.CloseSendMenu();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Send options â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    int _sendStep; // 0 closed, 1 "Speichern / SpÃ¤ter senden", 2 the times
    int _sendGeneration;

    /// <summary>
    /// The capsules slide up out of the send button one after the other (nearest first) with a slight overshoot,
    /// and slide back into it when closed. "SpÃ¤ter senden" swaps them for the times the same way.
    /// </summary>
    async void UpdateSendMenu()
    {
        var step = !_vm.IsSendMenuOpen ? 0 : _vm.IsSendLaterOpen ? 2 : 1;
        if (step == _sendStep) return;
        var previous = _sendStep;
        _sendStep = step;
        var generation = ++_sendGeneration;

        if (previous != 0)
        {
            var leaving = previous == 1 ? (Control)SendMenu : SendTimesMenu;
            await Task.WhenAll(Capsules(leaving).Select(c => Motion.Out(c, dy: 14, ms: 120)));
            if (generation != _sendGeneration) return;
            leaving.IsVisible = false;
        }
        if (step == 0) return;

        var coming = step == 1 ? (Control)SendMenu : SendTimesMenu;
        // Above the reply field (it is taller while it has the keyboard), so no capsule covers the text.
        var above = Math.Max(42, QuickReplyBox.Bounds.Height + 8);
        Canvas.SetBottom(coming, above);
        coming.Opacity = 0; // no flash of the finished stack before the first frame
        coming.IsVisible = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); // the times need their containers
        if (generation != _sendGeneration) return;
        var capsules = Capsules(coming);
        foreach (var c in capsules) c.Opacity = 0;
        coming.Opacity = 1;
        for (var i = 0; i < capsules.Count; i++)
        {
            var capsule = capsules[capsules.Count - 1 - i]; // the lowest one, right above the button, first
            var distance = above - 16 + i * 44.0; // starts down at the button
            if (i > 0) await Task.Delay(32);
            if (generation != _sendGeneration) return;
            _ = Motion.Animate(capsule, 0, 1, distance, 0, 260, Motion.EaseOutBack);
        }
    }

    static IReadOnlyList<Control> Capsules(Control host) => host switch
    {
        Panel panel => panel.Children.ToList(),
        // The wrapper around each capsule, not the container: it leaves room for the shadow while fading.
        ItemsControl items => items.GetRealizedContainers().Select(c => (c as Avalonia.Controls.Presenters.ContentPresenter)?.Child ?? c).ToList(),
        _ => [],
    };

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Reader â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    long? _renderedId;

    /// <summary>Another mail was chosen: the old content goes at once, it must not stand under the new header.</summary>
    void OnCurrentMessageChanged()
    {
        if (_vm.CurrentMessageId is not null && _vm.CurrentMessageId == _renderedId) return;
        Motion.Stop(ReaderBody);
        ReaderBody.Opacity = 0;
        _renderedId = null;
    }

    /// <summary>New content is in place: a different mail fades in, the same one (images loaded â€¦) just stays.</summary>
    void RevealBody(bool newMail)
    {
        if (newMail) _ = Motion.In(ReaderBody, dy: 6, ms: 180);
        else Motion.Reset(ReaderBody);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Toast â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    bool _toastShown;

    void UpdateToast()
    {
        if (_vm.Notice is { } text)
        {
            ToastText.Text = text;
            ToastAction.Content = _vm.NoticeActionLabel;
            ToastAction.IsVisible = _vm.NoticeActionLabel is not null;
            PlaceToast();
            if (_toastShown) return;
            _toastShown = true;
            Toast.IsVisible = true;
            _ = Motion.In(Toast, dy: 12, ms: 220);
            return;
        }
        if (!_toastShown) return;
        _toastShown = false;
        HideToastAsync();
    }

    /// <summary>Above the quick reply field, never on top of it – also when the field grows while typing.</summary>
    void PlaceToast()
    {
        // The field sits 14 px above the window edge; the toast keeps 24 px from its top.
        var bottom = _vm.CanQuickReply && QuickReplyBox.IsEffectivelyVisible ? QuickReplyBox.Bounds.Height + 38 : 24;
        Toast.Margin = new Thickness(16, 0, 16, bottom);
    }

    async void HideToastAsync()
    {
        await Motion.Out(Toast, dy: 8, ms: 160);
        if (!_toastShown) Toast.IsVisible = false; // a new toast may have come in meanwhile
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Command field â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    IInputElement? _focusBeforePalette;

    void UpdatePalette()
    {
        if (_vm.IsPaletteOpen)
        {
            _focusBeforePalette = FocusManager?.GetFocusedElement();
            PaletteLayer.IsVisible = true;
            _ = Motion.In(PaletteBackdrop, dy: 0, ms: 140);
            _ = Motion.In(PaletteCard, dy: -8, ms: 180);
            Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Loaded);
            return;
        }
        ClosePaletteAsync();
    }

    async void ClosePaletteAsync()
    {
        // The keyboard goes back where it was; if that is gone (e.g. a folder switch), to the mail list.
        if (_focusBeforePalette is Control { IsEffectivelyVisible: true } back && TopLevel.GetTopLevel(back) == this) back.Focus();
        else FocusMessageList();
        _focusBeforePalette = null;
        _ = Motion.Out(PaletteBackdrop, dy: 0, ms: 120);
        await Motion.Out(PaletteCard, dy: -6, ms: 120);
        if (!_vm.IsPaletteOpen) PaletteLayer.IsVisible = false;
    }

    /// <summary>â†‘/â†“ choose, Enter runs, Esc closes. Everything else goes to the text field.</summary>
    bool OnPaletteKey(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Escape:
                _vm.ClosePalette();
                return true;
            case Key.Down:
            case Key.Tab when mods == KeyModifiers.None:
                _vm.StepPalette(1);
                ScrollPaletteSelection();
                return true;
            case Key.Up:
            case Key.Tab when mods == KeyModifiers.Shift:
                _vm.StepPalette(-1);
                ScrollPaletteSelection();
                return true;
            case Key.Enter:
                _vm.RunPalette();
                return true;
        }
        return false;
    }

    void ScrollPaletteSelection()
    {
        if (_vm.SelectedPaletteEntry is { } entry) PaletteList.ScrollIntoView(entry);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Tab pill â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    bool _pillPlaced;
    double _pillX = double.NaN, _pillWidth = double.NaN;

    /// <summary>
    /// Puts the raised pill under the chosen tab. The first time without animation (it must not fly in from the
    /// left when the inbox opens), afterwards it glides. Compared with the target, not the animated value, so the
    /// layout passes during the glide do not restart it.
    /// </summary>
    void PlaceTabPill()
    {
        if (!TabItems.IsEffectivelyVisible)
        {
            _pillPlaced = false;
            return;
        }
        var index = -1;
        for (var i = 0; i < _vm.CategoryTabs.Count; i++)
            if (_vm.CategoryTabs[i].IsSelected) index = i;
        if (index < 0 || TabItems.ContainerFromIndex(index) is not Control tab || tab.Bounds.Width <= 0) return;
        if (tab.TranslatePoint(default, TabTrack) is not { } origin) return;

        var x = Math.Round(origin.X, 1);
        var width = Math.Round(tab.Bounds.Width, 1);
        if (_pillPlaced && x == _pillX && width == _pillWidth) return;
        _pillX = x;
        _pillWidth = width;
        TabPill.Classes.Set("animated", _pillPlaced);
        TabPill.Width = width;
        TabPill.Margin = new Thickness(x, 0, 0, 0);
        _pillPlaced = true;
    }
}
