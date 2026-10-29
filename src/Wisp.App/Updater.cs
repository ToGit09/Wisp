using Velopack;
using Velopack.Sources;

namespace Wisp.App;

/// <summary>
/// Updates from the GitHub releases via Velopack. A new version is downloaded in the background
/// and installed on the next start (or right away with <see cref="RestartToUpdate"/>).
/// </summary>
static class Updater
{
    const string RepoUrl = "https://github.com/ToGit09/Wisp";

    static readonly UpdateManager Manager = new(new GithubSource(RepoUrl, null, false));

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
}
