using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wisp.App.Views;

/// <summary>A small yes/no dialog. The safe choice ("Abbrechen") is the default button.</summary>
public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirm, bool danger)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Abbrechen", IsDefault = true, IsCancel = true };
        var ok = new Button { Content = confirm, Classes = { "primary" } };
        if (danger) ok.Background = owner.Brush("WispDanger");
        cancel.Click += (_, _) => dialog.Close(false);
        ok.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, Classes = { "title" } },
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
        return dialog.ShowDialog<bool>(owner);
    }
}
