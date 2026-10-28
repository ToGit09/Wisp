using Wisp.Core;

namespace Wisp.App;

/// <summary>
/// Writes errors to crash.log in the data folder for troubleshooting. Only exception types and stack
/// traces are written — no messages, which could contain parts of a mail.
/// </summary>
static class CrashLog
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    /// <summary>Settings → Datenschutz → "Fehlerprotokoll schreiben".</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(Exception exception)
    {
        if (!Enabled) return;
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "crash.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                using var writer = File.AppendText(path);
                writer.WriteLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Wisp {typeof(CrashLog).Assembly.GetName().Version}");
                for (var ex = exception; ex is not null; ex = ex.InnerException)
                {
                    writer.WriteLine(ex.GetType().FullName);
                    writer.WriteLine(ex.StackTrace);
                }
                AppPaths.RestrictFile(path);
            }
        }
        catch
        {
            // logging must never cause a crash itself
        }
    }
}
