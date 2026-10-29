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
}
