using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Wisp.App.Theme;

/// <summary>Draws a stroke icon defined on a 24×24 grid in the current text colour.</summary>
public sealed class WispIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<WispIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<WispIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<WispIcon, double>(nameof(StrokeThickness), 1.8);

    static WispIcon()
    {
        AffectsRender<WispIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<WispIcon>(16);
        HeightProperty.OverrideDefaultValue<WispIcon>(16);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        // StrokeThickness is meant for a 16 px icon; in grid units that is ×1.5, and it scales with the icon.
        var pen = new Pen(Foreground, StrokeThickness * 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, Data);
    }
}
