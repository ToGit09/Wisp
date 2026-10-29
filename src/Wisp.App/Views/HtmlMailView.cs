using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Wisp.App.Views;

/// <summary>
/// Shows an HTML mail scaled to the reader: mails designed for a fixed width (newsletters are
/// often 600–700 px) are shrunk until they fit, and the user can zoom on top of that.
/// </summary>
public sealed class HtmlMailView : Grid
{
    public const double MinZoom = 0.5;
    public const double MaxZoom = 2.0;
    const double Step = 0.1;
    const double PaperPadding = 20;
    const double PaperMargin = 14;
    /// <summary>Widest the paper gets at 100 % (like a printed page); wider readers centre it.</summary>
    const double MaxPaperWidth = 860;

    /// <summary>Content this much wider than the space it was given counts as fixed-width and gets shrunk.</summary>
    const double OverflowTolerance = 8;

    readonly ScrollViewer _scroll;
    readonly LayoutTransformControl _transform;
    readonly Border _paper;
    readonly TextBlock _zoomText;
    readonly string _html;
    readonly TheArtOfDev.HtmlRenderer.Core.CssData _css;
    double _zoom;
    double _available;
    bool _refitQueued;

    /// <summary>The renderer itself; the window hooks image loading and link clicks onto it.</summary>
    public HtmlLabel Html { get; }

    /// <summary>Raised when the user changes the zoom, so it can be remembered.</summary>
    public event Action<double>? ZoomChanged;

    public HtmlMailView(string html, string baseStylesheet, double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _html = html;
        _css = HtmlRender.ParseStyleSheet(baseStylesheet, true);

        // No Padding on the renderer itself: HtmlLabel lays its content out at (0,0) but only applies the
        // padding offset when painting, so the first frame can come out shifted and clipped at the top left.
        // The margin lives on the surrounding "paper" instead.
        Html = new HtmlLabel
        {
            Text = html,
            BaseStylesheet = baseStylesheet,
            AutoSize = false,
            AutoSizeHeightOnly = true,
            Background = Brushes.White,
            IsContextMenuEnabled = false,
        };
        // HTML mails are designed for a light background; show them on a white sheet of "paper",
        // centred in the reader, in dark mode too.
        _paper = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(PaperPadding),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Child = Html,
        };
        _transform = new LayoutTransformControl
        {
            Child = _paper,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(PaperMargin, PaperMargin / 2, PaperMargin, PaperMargin * 2),
        };
        _scroll = new ScrollViewer
        {
            Content = _transform,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        this.Bind(BackgroundProperty, this.GetResourceObservable("WispSurface"));
        // Zoom controls get their own strip above the mail: floating on top, they covered its text in narrow readers.
        RowDefinitions = new RowDefinitions("Auto,*");
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        _zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
        var zoomBar = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 8, 22, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    ZoomButton("−", "Verkleinern (Strg+Minus)", () => SetZoom(_zoom - Step)),
                    _zoomText,
                    ZoomButton("+", "Vergrößern (Strg+Plus)", () => SetZoom(_zoom + Step)),
                    ZoomButton("100 %", "Originalgröße (Strg+0)", () => SetZoom(1.0)),
                },
            },
        };
        Children.Add(zoomBar);

        // Never resize from inside a layout pass: queue it, so layout always settles in one go.
        _scroll.SizeChanged += (_, _) => QueueRefit();
        // Ctrl + mouse wheel zooms, like in a browser.
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SetZoom(_zoom + (e.Delta.Y > 0 ? Step : -Step));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add: SetZoom(_zoom + Step); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom - Step); e.Handled = true; break;
                case Key.D0 or Key.NumPad0: SetZoom(1.0); e.Handled = true; break;
            }
        };
        UpdateZoomText(_zoom);
    }

    Button ZoomButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, Classes = { "icon" }, Padding = new Thickness(8, 4) };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    void SetZoom(double zoom)
    {
        zoom = Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 2);
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        _zoom = zoom;
        Refit(force: true);
        ZoomChanged?.Invoke(zoom);
    }

    void QueueRefit()
    {
        if (_refitQueued) return;
        _refitQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refitQueued = false;
            Refit(force: false);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Lays the mail out for the reader width at the chosen zoom. The mail is measured once, up front,
    /// at the width it would get; if it needs more (fixed-width tables, large images) it gets its natural
    /// width and is shrunk to fit. Width and scale are set in one step, never adjusted from layout callbacks.
    /// </summary>
    void Refit(bool force)
    {
        // Minus the margin around the paper, and a little slack so rounding never forces a horizontal scrollbar.
        var available = Math.Floor(_scroll.Bounds.Width) - 2 * PaperMargin - 2;
        if (available <= 0 || (!force && Math.Abs(available - _available) < 1)) return;
        _available = available;

        var padding = _paper.Padding.Left + _paper.Padding.Right + _paper.BorderThickness.Left + _paper.BorderThickness.Right;
        var width = Math.Min(available / _zoom, MaxPaperWidth);
        var natural = MeasureContent(width - padding) + padding;
        var scale = _zoom;
        if (natural > width + OverflowTolerance)
        {
            width = Math.Ceiling(natural);
            scale = Math.Max(MinZoom / 2, Math.Min(_zoom, available / width));
        }

        _paper.Width = width;
        _transform.LayoutTransform = new ScaleTransform(scale, scale);
        UpdateZoomText(scale);
    }

    /// <summary>Width the mail needs when given <paramref name="maxWidth"/>. Blocks all images and style sheets while measuring.</summary>
    double MeasureContent(double maxWidth)
    {
        try
        {
            return HtmlRender.Measure(_html, Math.Max(1, maxWidth), _css,
                (_, e) => e.SetStyleSheet = "",
                (_, e) => { e.Handled = true; e.Callback(); }).Width;
        }
        catch (Exception)
        {
            return maxWidth; // unmeasurable: just show it at the reader width
        }
    }

    void UpdateZoomText(double scale) => _zoomText.Text = $"{Math.Round(scale * 100):0} %";
}
