using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wisp.App.Views;

/// <summary>Asks for one line of text. Returns null when cancelled.</summary>
public static class PromptDialog
{
    public static Task<string?> ShowAsync(Window owner, string title, string label, string placeholder, Func<string, string?>? validate = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var input = new TextBox { PlaceholderText = placeholder };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Foreground = owner.Brush("WispDanger") };
        var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(null);
        ok.Click += (_, _) =>
        {
            var text = input.Text ?? "";
            if (validate?.Invoke(text) is { } problem)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            dialog.Close(text);
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, Classes = { "title" } },
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                input,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
        dialog.Opened += (_, _) => input.Focus();
        return dialog.ShowDialog<string?>(owner);
    }
}
