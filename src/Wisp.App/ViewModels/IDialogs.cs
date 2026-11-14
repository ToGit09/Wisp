using Wisp.Core.Mail;

namespace Wisp.App.ViewModels;

/// <summary>Everything a view model needs from the window it lives in.</summary>
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false);
    /// <param name="restored">True for a mail taken back with "Rückgängig": reopen it exactly as it was.</param>
    /// <param name="typed">Text already written (quick reply): goes above signature and quote, the caret after it.</param>
    void OpenCompose(Draft draft, bool restored = false, string? typed = null);
    Task OpenSettingsAsync();
    Task OpenAccountSetupAsync();
    Task<Stream?> SaveFileAsync(string suggestedName);
    /// <summary>
    /// "Speichern unter" for one mail, with the file type to choose (.eml, .html, .txt). The name the user ended
    /// up with decides the format. Null if cancelled.
    /// </summary>
    Task<(Stream Stream, string Name)?> SaveExportAsync(string suggestedName);
    /// <summary>Asks for a folder (several mails are exported into one). Null if cancelled or not a local folder.</summary>
    Task<string?> PickFolderAsync(string title);
    Task OpenLinkAsync(string url, bool suspicious);
    /// <summary>Puts a one-time code on the clipboard (kept out of clipboard history where possible).</summary>
    Task CopySecretAsync(string text);
    /// <summary>Empties the clipboard, but only if it still holds <paramref name="text"/>.</summary>
    Task ClearClipboardIfAsync(string text);
}
