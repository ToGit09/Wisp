using Avalonia.Controls;
using Wisp.App.ViewModels;

namespace Wisp.App.Views;

public partial class AccountSetupWindow : Window
{
    public AccountSetupWindow() : this(null!) { }

    public AccountSetupWindow(AccountSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        if (vm is null) return;
        vm.Completed += Close;
        Closing += (_, _) => vm.Cancel();
        Opened += (_, _) => EmailBox.Focus();
    }
}
