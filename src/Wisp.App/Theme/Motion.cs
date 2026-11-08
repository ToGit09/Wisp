using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wisp.App.Theme;

/// <summary>
/// Small fade-and-slide animations driven by the window's render loop. Each control runs at most one at a
/// time: a new one starts from wherever the previous one was, so quick repeated changes (arrowing through
/// the list) never stack up, jump or flicker. Keep them short (≤ 250 ms) and the distance small (≤ 12 px).
/// </summary>
public static class Motion
{
    sealed class Run { public bool Cancelled; }

    static readonly ConditionalWeakTable<Visual, Run> Running = new();

    public static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
    public static double EaseIn(double t) => t * t * t;

    /// <summary>Appears: opacity 0 → 1 while moving <paramref name="dy"/> px into place.</summary>
    public static Task In(Visual visual, double dy = 6, int ms = 180) =>
        Animate(visual, 0, 1, dy, 0, ms, EaseOut);

    /// <summary>Disappears: fades out from its current opacity while moving <paramref name="dy"/> px away.</summary>
    public static Task Out(Visual visual, double dy = 6, int ms = 140) =>
        Animate(visual, visual.Opacity, 0, CurrentY(visual), dy, ms, EaseIn);

    /// <summary>Stops a running animation and leaves the control where it is.</summary>
    public static void Stop(Visual visual)
    {
        if (Running.TryGetValue(visual, out var run)) run.Cancelled = true;
        Running.Remove(visual);
    }

    /// <summary>Stops a running animation and shows the control fully, at its place.</summary>
    public static void Reset(Visual visual, double opacity = 1)
    {
        Stop(visual);
        visual.Opacity = opacity;
        if (visual.RenderTransform is TranslateTransform t) t.Y = 0;
    }

    public static Task Animate(Visual visual, double fromOpacity, double toOpacity, double fromY, double toY, int ms, Func<double, double> ease)
    {
        Stop(visual);
        var translate = visual.RenderTransform as TranslateTransform;
        if (translate is null && visual.RenderTransform is null && (fromY != 0 || toY != 0))
            visual.RenderTransform = translate = new TranslateTransform();

        void Set(double e)
        {
            visual.Opacity = fromOpacity + (toOpacity - fromOpacity) * e;
            if (translate is not null) translate.Y = fromY + (toY - fromY) * e;
        }

        var top = TopLevel.GetTopLevel(visual);
        if (top is null || ms <= 0)
        {
            Set(1);
            return Task.CompletedTask;
        }

        var run = new Run();
        Running.AddOrUpdate(visual, run);
        var done = new TaskCompletionSource();
        TimeSpan? start = null;
        Set(0);

        void Frame(TimeSpan now)
        {
            if (run.Cancelled)
            {
                done.TrySetResult();
                return;
            }
            start ??= now;
            var t = Math.Clamp((now - start.Value).TotalMilliseconds / ms, 0, 1);
            Set(ease(t));
            if (t < 1)
            {
                top.RequestAnimationFrame(Frame);
                return;
            }
            if (Running.TryGetValue(visual, out var current) && current == run) Running.Remove(visual);
            done.TrySetResult();
        }

        top.RequestAnimationFrame(Frame);
        return done.Task;
    }

    static double CurrentY(Visual visual) => visual.RenderTransform is TranslateTransform t ? t.Y : 0;
}
