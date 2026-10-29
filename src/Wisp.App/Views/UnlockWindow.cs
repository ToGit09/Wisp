using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wisp.Core.Security;

namespace Wisp.App.Views;

/// <summary>Asks for the master password, or lets the user create one when the OS offers no key store.</summary>
public sealed class UnlockWindow : Window
{
    readonly Vault _vault;
    readonly bool _create;
    readonly TextBox _password = new() { PasswordChar = '•', PlaceholderText = "Master-Passwort" };
    readonly TextBox _repeat = new() { PasswordChar = '•', PlaceholderText = "Master-Passwort wiederholen" };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _ok;

    public byte[]? Key { get; private set; }

    public UnlockWindow(Vault vault, bool create)
    {
        _vault = vault;
        _create = create;
        Title = "Wisp";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://{typeof(UnlockWindow).Assembly.GetName().Name}/Assets/wisp.ico")));

        _ok = new Button { Content = create ? "Festlegen" : "Entsperren", Classes = { "primary" }, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        _ok.Click += (_, _) => Submit();
        _error.Foreground = this.Brush("WispDanger") ?? Brushes.Red;
        _repeat.IsVisible = create;

        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 12,
            Children =
            {
                new Image { Source = new Bitmap(AssetLoader.Open(new Uri($"avares://{typeof(UnlockWindow).Assembly.GetName().Name}/Assets/wisp.png"))), Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = create ? "Master-Passwort festlegen" : "Wisp entsperren", Classes = { "title" } },
                new TextBlock
                {
                    Classes = { "hint" },
                    Text = create
                        ? "Auf diesem System gibt es keinen Schlüsselbund. Wisp schützt Ihre Daten deshalb mit einem Master-Passwort, nach dem es bei jedem Start fragt. Ein vergessenes Passwort kann niemand wiederherstellen."
                        : "Ihre Mails sind mit Ihrem Master-Passwort verschlüsselt.",
                },
                _password,
                _repeat,
                _error,
                _ok,
            },
        };
        Opened += (_, _) => _password.Focus();
    }

    void Submit()
    {
        _error.IsVisible = false;
        if (_create)
        {
            if (_password.Text is not { Length: >= 8 } pw) { Show("Bitte mindestens 8 Zeichen verwenden."); return; }
            if (pw != _repeat.Text) { Show("Die Passwörter stimmen nicht überein."); return; }
            Key = _vault.Create(VaultProtection.MasterPassword, pw);
            Close();
            return;
        }

        _ok.IsEnabled = false;
        try
        {
            Key = _vault.UnlockWithPassword(_password.Text ?? "");
            Close();
        }
        catch (CryptographicException)
        {
            Show("Das Passwort ist falsch.");
            _password.Text = "";
        }
        finally
        {
            _ok.IsEnabled = true;
        }
    }

    void Show(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
