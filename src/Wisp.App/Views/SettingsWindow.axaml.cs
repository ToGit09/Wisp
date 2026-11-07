using Avalonia.Controls;
using Wisp.App.ViewModels;

namespace Wisp.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(null!) { }

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closed += (_, _) => vm?.Detach();
        // Esc closes (changes are already saved). Bubbling, so an open dropdown closes first.
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || e.KeyModifiers != Avalonia.Input.KeyModifiers.None) return;
            e.Handled = true;
            Close();
        };
    }
}
