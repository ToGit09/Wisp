using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TheArtOfDev.HtmlRenderer.Avalonia;
using Wisp.Core.Rendering;

namespace Wisp.App.Views;

/// <summary>What the reader shows of a mail, as far as selecting and copying goes (HTML or text view).</summary>
public interface IMailBody
{
    string SelectedText { get; }
    void SelectAll();
    /// <summary>Address of the link at this point of the body (in its own coordinates), if there is one.</summary>
    string? LinkAt(Point point);
}

/// <summary>
/// Shows an HTML mail scaled to the reader: mails designed for a fixed width (newsletters are
/// often 600–700 px) are shrunk until they fit, and the user can zoom on top of that.
/// Text can be selected with the mouse (double-click: a word) and copied.
/// </summary>
public sealed class HtmlMailView : Grid, IMailBody
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

    /// <summary>
    /// What a mail looks like where it says nothing itself: readable type, quotes with a quiet line on the left
    /// (the renderer only indents them), code in a fixed-width font, no frames around linked images.
    /// </summary>
    const string BaseStylesheet = """
        body { font-family: Segoe UI, Ubuntu, Cantarell, sans-serif; font-size: 10.5pt; color: #1d1f24; }
        a { color: #2f5fd0; }
        a img, img { border: 0; }
        blockquote { margin: 8px 0 8px 2px; padding: 0 0 0 12px; border-left: 2px solid #cfd4de; color: #4b5160; }
        pre, code, tt { font-family: Cascadia Mono, Consolas, DejaVu Sans Mono, monospace; font-size: 9.5pt; }
        pre { background-color: #f3f4f7; padding: 8px 10px; corner-radius: 6px; white-space: pre; }
        hr { border: 0; border-top: 1px solid #dfe2e8; height: 1px; }
        h1 { font-size: 17pt; } h2 { font-size: 14.5pt; } h3 { font-size: 12.5pt; }
        """;

    static readonly TheArtOfDev.HtmlRenderer.Core.CssData Css = HtmlRender.ParseStyleSheet(BaseStylesheet, true);

    readonly ScrollViewer _scroll;
    readonly LayoutTransformControl _transform;
    readonly Border _paper;
    readonly TextBlock _zoomText;
    readonly RenderedMail _mail;
    readonly string _html;
    double _zoom;
    double _available;
    bool _refitQueued;

    /// <summary>The renderer itself.</summary>
    public HtmlLabel Html { get; }

    /// <summary>Raised when the user changes the zoom, so it can be remembered.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>A link in the mail was clicked.</summary>
    public event Action<string>? LinkClicked;

    public HtmlMailView(RenderedMail mail, double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _mail = mail;
        _html = mail.Html ?? "";
        Focusable = true; // so Strg+A / Strg+C after a click into the mail mean the mail, not the list

        // HTML mails are designed for a light background; show them on a sheet of "paper" in the colour the
        // mail asks for (white unless it says otherwise), centred in the reader, in dark mode too.
        var sheet = Color.TryParse(mail.HtmlBackground, out var asked) && asked.A == 255 ? asked : Colors.White;
        var sheetBrush = new SolidColorBrush(sheet);

        // No Padding on the renderer itself: HtmlLabel lays its content out at (0,0) but only applies the
        // padding offset when painting, so the first frame can come out shifted and clipped at the top left.
        // The margin lives on the surrounding "paper" instead.
        Html = new HtmlLabel
        {
            Text = _html,
            BaseStylesheet = BaseStylesheet,
            AutoSize = false,
            AutoSizeHeightOnly = true,
            Background = sheetBrush,
            IsContextMenuEnabled = false,
            IsSelectionEnabled = true,
        };
        _paper = new Border
        {
            Background = sheetBrush,
            Padding = new Thickness(PaperPadding),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = Html,
        };
        _paper.Bind(Border.BorderBrushProperty, this.GetResourceObservable("WispBorder"));
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

        // The percentage itself resets to 100 % (a separate "100 %" button next to it read like a second display).
        _zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
        var reset = new Button { Content = _zoomText, Classes = { "icon" }, Padding = new Thickness(4, 4) };
        ToolTip.SetTip(reset, "Originalgröße (Strg+0)");
        reset.Click += (_, _) => SetZoom(1.0);
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
                    reset,
                    ZoomButton("+", "Vergrößern (Strg+Plus)", () => SetZoom(_zoom + Step)),
                },
            },
        };
        Children.Add(zoomBar);

        Html.ImageLoad += (_, e) => LoadImage(e.Event);
        Html.StylesheetLoad += (_, e) => e.Event.SetStyleSheet = ""; // never fetch external CSS
        Html.LinkClicked += (_, e) =>
        {
            e.Event.Handled = true;
            if (e.Event.Link is { Length: > 0 } url) LinkClicked?.Invoke(url);
        };
        // A click into the mail takes the keyboard (the renderer handles the click itself, so this also sees handled ones).
        AddHandler(PointerPressedEvent, (_, _) => Focus(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

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

    // ───────────────────────── Selecting & copying ─────────────────────────

    public string SelectedText => Html.SelectedText ?? "";

    /// <summary>The renderer selects everything on Strg+A; it has no method for it, so it is handed that key.</summary>
    public void SelectAll()
    {
        Html.Container?.HandleKeyDown(Html, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control, Source = Html });
        Html.InvalidateVisual();
    }

    public string? LinkAt(Point point) =>
        this.TranslatePoint(point, Html) is { } local && Html.Container?.GetLinkAt(local) is { Length: > 0 } link ? link : null;

    // ───────────────────────── Images ─────────────────────────

    void LoadImage(TheArtOfDev.HtmlRenderer.Core.Entities.HtmlImageLoadEventArgs args)
    {
        var src = args.Src ?? "";
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return; // decoded locally by the renderer
        args.Handled = true;
        // An image without a size of its own is drawn pixel for pixel: a photo from a phone made the whole mail
        // several thousand pixels wide (and it was then shrunk to unreadable). Such images are fitted to the sheet.
        var sized = args.Attributes is { } attributes && (attributes.ContainsKey("width")
            || attributes.TryGetValue("style", out var style) && style.Contains("width", StringComparison.OrdinalIgnoreCase));

        if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            args.Callback(_mail.InlineImages.TryGetValue(src[4..].Trim('<', '>'), out var bytes) && TryBitmap(bytes, sized) is { } inline ? inline : Blank);
            return;
        }
        if (!_mail.RemoteImagesAllowed || !HtmlSanitizer.IsRemote(src))
        {
            args.Callback(Blank); // blocked: nothing is contacted, and no "broken image" icon either
            return;
        }
        var ct = _closed.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await RemoteImageLoader.LoadAsync(src, ct);
                var bitmap = data is null ? null : TryBitmap(data, sized); // decoded off the UI thread
                if (ct.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() => args.Callback(bitmap ?? Blank));
            }
            catch (OperationCanceledException) { /* another mail is open by now */ }
        });
    }

    // Images still on their way when another mail is opened are of no use any more: stop loading them.
    readonly CancellationTokenSource _closed = new();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _closed.Cancel();
    }

    static Bitmap? TryBitmap(byte[] data, bool sized)
    {
        try
        {
            var bitmap = new Bitmap(new MemoryStream(data));
            var max = (int)(MaxPaperWidth - 2 * PaperPadding - 2);
            if (sized || bitmap.PixelSize.Width <= max) return bitmap;
            using (bitmap)
                return bitmap.CreateScaledBitmap(new PixelSize(max, Math.Max(1, (int)Math.Round((double)bitmap.PixelSize.Height * max / bitmap.PixelSize.Width))));
        }
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

    // ───────────────────────── Zoom ─────────────────────────

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
        Dispatcher.UIThread.Post(() =>
        {
            _refitQueued = false;
            Refit(force: false);
        }, DispatcherPriority.Background);
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
        // The natural width does not depend on the zoom or the reader: measured once per offered width.
        if (Math.Abs(width - _measuredFor) > 0.5)
        {
            _natural = MeasureContent(width - padding) + padding;
            _measuredFor = width;
        }
        var scale = _zoom;
        if (_natural > width + OverflowTolerance)
        {
            width = Math.Ceiling(_natural);
            scale = Math.Max(MinZoom / 2, Math.Min(_zoom, available / width));
        }

        _paper.Width = width;
        _transform.LayoutTransform = new ScaleTransform(scale, scale);
        UpdateZoomText(scale);
    }

    double _natural, _measuredFor = -1;

    /// <summary>Width the mail needs when given <paramref name="maxWidth"/>. Blocks all images and style sheets while measuring.</summary>
    double MeasureContent(double maxWidth)
    {
        try
        {
            return HtmlRender.Measure(_html, Math.Max(1, maxWidth), Css,
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
