using Wisp.Core.Mail;

namespace Wisp.App.ViewModels;

/// <summary>Everything a view model needs from the window it lives in.</summary>
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false);
    void OpenCompose(Draft draft);
    Task OpenSettingsAsync();
    Task OpenAccountSetupAsync();
    Task<Stream?> SaveFileAsync(string suggestedName);
    Task OpenLinkAsync(string url, bool suspicious);
    /// <summary>Puts a one-time code on the clipboard (kept out of clipboard history where possible).</summary>
    Task CopySecretAsync(string text);
    /// <summary>Empties the clipboard, but only if it still holds <paramref name="text"/>.</summary>
    Task ClearClipboardIfAsync(string text);
}
