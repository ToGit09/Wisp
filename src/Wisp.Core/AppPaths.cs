namespace Wisp.Core;

/// <summary>Where Wisp keeps its files. WISP_HOME overrides everything (portable mode, tests).</summary>
public static class AppPaths
{
    static string? _override = Environment.GetEnvironmentVariable("WISP_HOME");

    public static string DataDir => Ensure(_override is not null
        ? Path.Combine(_override, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    public static string ConfigDir => Ensure(_override is not null
        ? Path.Combine(_override, "config")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    public static string DatabaseFile => Path.Combine(DataDir, "wisp.db");
    public static string VaultFile => Path.Combine(DataDir, "vault.json");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    static string AppFolderName => OperatingSystem.IsWindows() ? "Wisp" : "wisp";

    public static void UseRoot(string root) => _override = root;

    /// <summary>Creates the directory and restricts it to the current user on Unix.</summary>
    public static string Ensure(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return dir;
    }

    /// <summary>Restricts a file to the current user on Unix (no-op on Windows, where the profile ACL applies).</summary>
    public static void RestrictFile(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
