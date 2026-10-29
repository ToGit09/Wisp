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
    }
}
