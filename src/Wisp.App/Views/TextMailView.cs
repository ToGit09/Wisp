using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wisp.Core.Rendering;

namespace Wisp.App.Views;

/// <summary>
/// The text view of a mail: paragraphs, quotes, lists – and below them the list of links with their real
/// addresses. The whole text behaves like one document: a selection runs across paragraphs (each paragraph is
/// its own text block, which on its own could only select inside itself), double-click takes a word,
/// triple-click a paragraph, and links in the text can be clicked.
/// </summary>
public sealed class TextMailView : ScrollViewer, IMailBody
{
    sealed record Link(int Start, int End, LinkInfo Info);
    sealed record Part(SelectableTextBlock Block, string Text, List<Link> Links);
    readonly record struct Position(int Part, int Char) : IComparable<Position>
    {
        public int CompareTo(Position other) => Part != other.Part ? Part.CompareTo(other.Part) : Char.CompareTo(other.Char);
    }

    static readonly Cursor TextCursor = new(StandardCursorType.Ibeam), HandCursor = new(StandardCursorType.Hand);

    readonly List<Part> _parts = [];
    readonly StackPanel _panel;
    Position _anchor, _caret;
    bool _dragging, _moved;
    Link? _pressedLink;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <summary>A link was clicked: in the text or in the list below it.</summary>
    public event Action<LinkInfo>? LinkClicked;

    /// <param name="showLinkList">List all links with their real address below the text.</param>
    /// <param name="warnSuspicious">Point out links whose text shows a different address than their target.</param>
    public TextMailView(RenderedMail mail, bool showLinkList, bool warnSuspicious, StyledElement theme)
    {
        Focusable = true;
        var accent = theme.Brush("WispAccent");
        var muted = theme.Brush("WispMuted");
        var border = theme.Brush("WispBorder");
        var fontSize = theme.FindResource("WispFontSize") as double? ?? 14.0;

        // Transparent, not null: the gaps between paragraphs belong to the document too (a drag may start there).
        _panel = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(24, 16, 24, 24), MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Cursor = TextCursor,
        };

        foreach (var block in mail.Blocks)
        {
            if (block.Kind == BlockKind.Separator)
            {
                _panel.Children.Add(new Border { Height = 1, Background = border, Margin = new Thickness(0, 6) });
                continue;
            }
            // The pointer is handled for the document as a whole, see below.
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, Focusable = false, LineHeight = Math.Round(fontSize * 1.5) };
            if (accent is not null) text.SelectionBrush = new SolidColorBrush(((ISolidColorBrush)accent).Color, 0.3);
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    text.FontWeight = FontWeight.SemiBold;
                    text.FontSize = fontSize + 3;
                    text.LineHeight = double.NaN;
                    break;
                case BlockKind.Quote:
                    text.Foreground = muted;
                    break;
                case BlockKind.Preformatted:
                    text.FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
                    text.LineHeight = double.NaN;
                    break;
            }

            var plain = new StringBuilder();
            var links = new List<Link>();
            void Add(Run run)
            {
                text.Inlines!.Add(run);
                plain.Append(run.Text);
            }
            if (block.Kind == BlockKind.ListItem) Add(new Run("•  "));
            foreach (var span in block.Spans)
            {
                var run = new Run(span.Text)
                {
                    FontWeight = span.Bold ? FontWeight.SemiBold : FontWeight.Normal,
                    FontStyle = span.Italic ? FontStyle.Italic : FontStyle.Normal,
                };
                var start = plain.Length;
                Add(run);
                if (span.LinkIndex < 0) continue;
                run.Foreground = accent;
                run.TextDecorations = TextDecorations.Underline;
                var link = mail.Links[span.LinkIndex];
                links.Add(new Link(start, plain.Length, link));
                if (link.Url != span.Text && link.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    Add(new Run($" [{link.Index + 1}]") { Foreground = muted, FontSize = 11 });
            }
            _parts.Add(new Part(text, plain.ToString(), links));

            _panel.Children.Add(block.Kind == BlockKind.Quote
                ? new Border { BorderBrush = border, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = text }
                : text);
        }

        if (mail.Blocks.Count == 0)
            _panel.Children.Add(new TextBlock { Text = "(Diese Mail hat keinen Textinhalt.)", Foreground = muted });

        var listed = mail.Links.Where(l => l.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (listed.Count > 0 && showLinkList)
        {
            _panel.Children.Add(new TextBlock { Text = "Links in dieser Mail", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
            foreach (var link in listed)
            {
                var row = new StackPanel { Spacing = 2 };
                var button = new Button
                {
                    Classes = { "link" },
                    Content = new TextBlock { Text = $"[{link.Index + 1}]  {link.Url}", TextWrapping = TextWrapping.Wrap },
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Tag = link,
                };
                button.Click += (_, _) => LinkClicked?.Invoke(link);
                row.Children.Add(button);
                if (link.Suspicious && warnSuspicious)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = $"⚠ Vorsicht: Der Linktext zeigt eine andere Adresse als das tatsächliche Ziel ({link.Host}).",
                        Foreground = theme.Brush("WispDanger"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                    });
                }
                _panel.Children.Add(row);
            }
        }

        Content = _panel;
        _panel.PointerPressed += OnPressed;
        _panel.PointerMoved += OnMoved;
        _panel.PointerReleased += OnReleased;
        _panel.PointerCaptureLost += (_, _) => _dragging = false;
    }

    // ───────────────────────── IMailBody ─────────────────────────

    public string SelectedText
    {
        get
        {
            var (from, to) = Ordered();
            if (from == to || _parts.Count == 0) return "";
            var sb = new StringBuilder();
            for (var i = from.Part; i <= to.Part; i++)
            {
                var text = _parts[i].Text;
                var start = i == from.Part ? Math.Min(from.Char, text.Length) : 0;
                var end = i == to.Part ? Math.Min(to.Char, text.Length) : text.Length;
                if (i > from.Part) sb.Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append(text, start, end - start);
            }
            return sb.ToString().ReplaceLineEndings().Trim('\r', '\n');
        }
    }

    public void SelectAll()
    {
        if (_parts.Count == 0) return;
        _anchor = new Position(0, 0);
        _caret = new Position(_parts.Count - 1, _parts[^1].Text.Length);
        ShowSelection();
    }

    public string? LinkAt(Point point)
    {
        var onPanel = this.TranslatePoint(point, _panel) ?? point;
        if (LinkUnder(onPanel) is { } link) return link.Info.Url;
        // The list of links below the text.
        return (this.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestorsUntil(this).OfType<Button>().FirstOrDefault()?.Tag is LinkInfo listed ? listed.Url : null;
    }

    // ───────────────────────── Pointer ─────────────────────────

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(_panel);
        if (!point.Properties.IsLeftButtonPressed) return; // a right-click keeps the selection for "Kopieren"
        var at = PositionAt(point.Position);
        _pressedLink = LinkUnder(point.Position);
        _moved = false;

        if (e.ClickCount == 2 && _parts.Count > 0) (_anchor, _caret) = WordAt(at);
        else if (e.ClickCount >= 3 && _parts.Count > 0) (_anchor, _caret) = (new Position(at.Part, 0), new Position(at.Part, _parts[at.Part].Text.Length));
        else
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _anchor = at;
            _caret = at;
            _dragging = true;
            e.Pointer.Capture(_panel);
        }
        ShowSelection();
        e.Handled = true;
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(_panel);
        if (!_dragging)
        {
            _panel.Cursor = LinkUnder(point) is null ? TextCursor : HandCursor;
            return;
        }
        var at = PositionAt(point);
        if (at == _caret) return;
        _caret = at;
        _moved = true;
        ShowSelection();
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        // A click on a link that did not turn into a selection opens it.
        if (!_moved && _anchor == _caret && _pressedLink is { } link && LinkUnder(e.GetPosition(_panel)) == link)
            LinkClicked?.Invoke(link.Info);
    }

    // ───────────────────────── Positions ─────────────────────────

    (Position From, Position To) Ordered() => _anchor.CompareTo(_caret) <= 0 ? (_anchor, _caret) : (_caret, _anchor);

    void ShowSelection()
    {
        var (from, to) = Ordered();
        for (var i = 0; i < _parts.Count; i++)
        {
            var (block, text, _) = _parts[i];
            var inside = from != to && i >= from.Part && i <= to.Part;
            block.SelectionStart = !inside ? 0 : i == from.Part ? from.Char : 0;
            block.SelectionEnd = !inside ? 0 : i == to.Part ? to.Char : text.Length;
        }
    }

    /// <summary>The text position nearest to a point of the panel; between two paragraphs it is the start of the lower one.</summary>
    Position PositionAt(Point point)
    {
        for (var i = 0; i < _parts.Count; i++)
        {
            var block = _parts[i].Block;
            if (block.TranslatePoint(default, _panel) is not { } origin) continue;
            if (point.Y < origin.Y) return new Position(i, 0);
            if (point.Y > origin.Y + block.Bounds.Height) continue;
            var hit = block.TextLayout.HitTestPoint(point - origin - new Point(block.Padding.Left, block.Padding.Top));
            return new Position(i, Math.Clamp(hit.TextPosition, 0, _parts[i].Text.Length));
        }
        return _parts.Count == 0 ? default : new Position(_parts.Count - 1, _parts[^1].Text.Length);
    }

    Link? LinkUnder(Point point)
    {
        foreach (var (block, _, links) in _parts)
        {
            if (links.Count == 0 || block.TranslatePoint(default, _panel) is not { } origin) continue;
            if (!new Rect(origin, block.Bounds.Size).Contains(point)) continue;
            var hit = block.TextLayout.HitTestPoint(point - origin - new Point(block.Padding.Left, block.Padding.Top));
            return hit.IsInside ? links.FirstOrDefault(l => hit.TextPosition >= l.Start && hit.TextPosition < l.End) : null;
        }
        return null;
    }

    (Position, Position) WordAt(Position at)
    {
        var text = _parts[at.Part].Text;
        if (text.Length == 0) return (at, at);
        var index = Math.Clamp(at.Char, 0, text.Length - 1);
        // Addresses and web links count as one word ("anna.m-schmidt@web.de"), like in the reader header.
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '.' or '-' or '\'';
        if (!IsWord(text[index])) return (new Position(at.Part, index), new Position(at.Part, index + 1));
        var start = index;
        while (start > 0 && IsWord(text[start - 1])) start--;
        var end = index;
        while (end < text.Length && IsWord(text[end])) end++;
        while (end > start + 1 && text[end - 1] is '.' or '-' or '\'') end--;
        return (new Position(at.Part, start), new Position(at.Part, end));
    }
}

static class VisualAncestors
{
    /// <summary>The visual and its ancestors, up to (not including) <paramref name="stop"/>.</summary>
    public static IEnumerable<Visual> GetSelfAndVisualAncestorsUntil(this Visual visual, Visual stop)
    {
        for (Visual? v = visual; v is not null && v != stop; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            yield return v;
    }
}
