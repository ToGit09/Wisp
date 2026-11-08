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
/// and the gliding tab pill. Rules: appear with ease-out, leave with ease-in, ≤ 240 ms, ≤ 12 px; never show a
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
    }

    // ───────────────────────── Reader ─────────────────────────

    long? _renderedId;

    /// <summary>Another mail was chosen: the old content goes at once, it must not stand under the new header.</summary>
    void OnCurrentMessageChanged()
    {
        if (_vm.CurrentMessageId is not null && _vm.CurrentMessageId == _renderedId) return;
        Motion.Stop(ReaderBody);
        ReaderBody.Opacity = 0;
        _renderedId = null;
    }

    /// <summary>New content is in place: a different mail fades in, the same one (images loaded …) just stays.</summary>
    void RevealBody(bool newMail)
    {
        if (newMail) _ = Motion.In(ReaderBody, dy: 6, ms: 180);
        else Motion.Reset(ReaderBody);
    }

    // ───────────────────────── Toast ─────────────────────────

    bool _toastShown;

    void UpdateToast()
    {
        if (_vm.Notice is { } text)
        {
            ToastText.Text = text;
            ToastAction.Content = _vm.NoticeActionLabel;
            ToastAction.IsVisible = _vm.NoticeActionLabel is not null;
            // Above the quick reply field, never on top of it.
            Toast.Margin = new Thickness(16, 0, 16, _vm.CanQuickReply ? 76 : 24);
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

    async void HideToastAsync()
    {
        await Motion.Out(Toast, dy: 8, ms: 160);
        if (!_toastShown) Toast.IsVisible = false; // a new toast may have come in meanwhile
    }

    // ───────────────────────── Command field ─────────────────────────

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

    /// <summary>↑/↓ choose, Enter runs, Esc closes. Everything else goes to the text field.</summary>
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

    // ───────────────────────── Tab pill ─────────────────────────

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
