using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wisp.App.Theme;
using Wisp.App.Views;
using Wisp.Core;
using Wisp.Core.Security;
using Wisp.Core.Settings;

namespace Wisp.App;

public partial class App : Application
{
    MailService? _mail;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load(AppPaths.SettingsFile);
            ThemeManager.Apply(settings.Appearance);
            CrashLog.Enabled = settings.Privacy.WriteCrashLog;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => Shutdown();
            InstallSafetyNet(desktop);
            _ = StartAsync(desktop, settings);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// An error while drawing one mail must not close the app. UI errors are logged and the reader
    /// falls back to text; background errors are only logged. All state is on disk, so nothing is lost.
    /// </summary>
    static void InstallSafetyNet(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.Handled = true;
            (desktop.MainWindow as MainWindow)?.RecoverFromRenderError();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write(ex);
        };
    }

    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, AppSettings settings)
    {
        var vault = new Vault(AppPaths.VaultFile);
        byte[]? key;
        try
        {
            key = await UnlockAsync(vault);
        }
        catch (Exception ex)
        {
            await ShowFatalAsync("Wisp konnte Ihren Datenschlüssel nicht öffnen.\n\n" + ex.Message);
            desktop.Shutdown(1);
            return;
        }
        if (key is null)
        {
            desktop.Shutdown();
            return;
        }

        try
        {
            _mail = MailService.Open(key, settings);
            _mail.Start();
            var main = new MainWindow(_mail, vault);
            main.Closed += (_, _) => desktop.Shutdown();
            desktop.MainWindow = main;
            main.Show();
            if (settings.Privacy.CheckForUpdates) _ = Updater.CheckInBackgroundAsync();
        }
        catch (Exception ex)
        {
            await ShowFatalAsync("Wisp konnte nicht gestartet werden.\n\n" + ex);
            desktop.Shutdown(1);
        }
    }

    /// <summary>First start: create the vault silently (OS-protected). Later: unlock it, asking only if a master password is set.</summary>
    static async Task<byte[]?> UnlockAsync(Vault vault)
    {
        if (!vault.Exists)
        {
            if (Vault.SystemProtectionAvailable) return vault.Create(VaultProtection.System);
            return await AskAsync(new UnlockWindow(vault, create: true));
        }
        return vault.Protection == VaultProtection.MasterPassword
            ? await AskAsync(new UnlockWindow(vault, create: false))
            : vault.UnlockWithSystem();
    }

    static Task<byte[]?> AskAsync(UnlockWindow window)
    {
        var done = new TaskCompletionSource<byte[]?>();
        window.Closed += (_, _) => done.TrySetResult(window.Key);
        window.Show();
        return done.Task;
    }

    static Task ShowFatalAsync(string message)
    {
        var done = new TaskCompletionSource();
        var window = new Window
        {
            Title = "Wisp",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new SelectableTextBlock { Text = message, Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        };
        window.Closed += (_, _) => done.TrySetResult();
        window.Show();
        return done.Task;
    }

    void Shutdown()
    {
        if (_mail is null) return;
        var mail = _mail;
        _mail = null;
        try { mail.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { /* exiting anyway; all state is already on disk */ }
    }
}
