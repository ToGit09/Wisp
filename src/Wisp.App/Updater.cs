using Velopack;
using Velopack.Sources;

namespace Wisp.App;

public enum SwitchOutcome { Restarting, NotInstalled, NotAvailable, Failed }

/// <summary>
/// Updates from the GitHub releases via Velopack. A new version is downloaded in the background
/// and installed on the next start (or right away with <see cref="RestartToUpdate"/>).
/// Wisp 2 always asks for its own edition's channel: a light edition only ever gets light updates.
/// </summary>
static class Updater
{
    const string RepoUrl = "https://github.com/ToGit09/Wisp";

    static GithubSource Source => new(RepoUrl, null, false);

    static readonly UpdateManager Manager = new(Source, new UpdateOptions { ExplicitChannel = Edition.Channel(Edition.IsAi) });

    /// <summary>False when started via "dotnet run" or from a copied folder: then there is nothing to update.</summary>
    public static bool IsInstalled => Manager.IsInstalled;

    /// <summary>The downloaded version waiting to be installed, or null.</summary>
    public static string? Pending => IsInstalled ? Manager.UpdatePendingRestart?.Version.ToString() : null;

    /// <summary>Looks for a newer version and downloads it. Returns that version, or null if Wisp is up to date.</summary>
    public static async Task<string?> DownloadNewerAsync()
    {
        if (!IsInstalled) return null;
        var update = await Manager.CheckForUpdatesAsync();
        if (update is null) return null;
        await Manager.DownloadUpdatesAsync(update);
        return update.TargetFullRelease.Version.ToString();
    }

    /// <summary>Silent check at startup. Being offline or rate-limited by GitHub is normal, so errors are ignored.</summary>
    public static async Task CheckInBackgroundAsync()
    {
        try { await DownloadNewerAsync(); }
        catch (Exception) { /* next start tries again */ }
    }

    /// <summary>Hands the downloaded update to the Velopack updater, which installs it once Wisp has exited and starts it again.</summary>
    public static bool RestartToUpdate()
    {
        var pending = IsInstalled ? Manager.UpdatePendingRestart : null;
        if (pending is null) return false;
        Manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        return true;
    }

    /// <summary>
    /// Changes the edition: downloads the newest version of the other edition's channel (same version is fine)
    /// and restarts into it. Mails, accounts and settings stay where they are; only the program is replaced.
    /// </summary>
    public static async Task<SwitchOutcome> SwitchEditionAsync(bool toAi, Action<int>? progress, CancellationToken ct)
    {
        if (!IsInstalled) return SwitchOutcome.NotInstalled;
        try
        {
            var other = new UpdateManager(Source, new UpdateOptions { ExplicitChannel = Edition.Channel(toAi), AllowVersionDowngrade = true });
            var update = await other.CheckForUpdatesAsync();
            if (update is null) return SwitchOutcome.NotAvailable;
            await other.DownloadUpdatesAsync(update, progress, ct);
            other.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);
            return SwitchOutcome.Restarting;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SwitchOutcome.Failed;
        }
    }

    public static string Describe(SwitchOutcome outcome, bool toAi) => outcome switch
    {
        SwitchOutcome.Restarting => "Wisp startet neu …",
        SwitchOutcome.NotInstalled => "Diese Wisp-Kopie ist nicht installiert. Die " + Edition.NameOf(toAi) + " gibt es über die Setup-Datei auf GitHub.",
        SwitchOutcome.NotAvailable => "Die " + Edition.NameOf(toAi) + " ist auf GitHub noch nicht verfügbar.",
        _ => "GitHub ist gerade nicht erreichbar. Bitte später noch einmal versuchen.",
    };
}
