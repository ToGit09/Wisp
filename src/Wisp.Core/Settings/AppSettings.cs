using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Core.Settings;

public enum ThemeMode { System, Light, Dark }
public enum Density { Compact, Normal, Relaxed }

/// <summary>Look and feel. Contains no personal data, so it is stored as plain JSON.</summary>
public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "#4F6BED";
    public string Font { get; set; } = "Inter";
    public double FontSize { get; set; } = 14;
    public Density Density { get; set; } = Density.Normal;
    public bool ShowPreview { get; set; } = true;
}

public sealed class PrivacySettings
{
    /// <summary>Render HTML mails with layout instead of converting them to plain text. Off by default.</summary>
    public bool ShowHtml { get; set; }
    /// <summary>Load images from the internet automatically. Off by default (they can be used for tracking).</summary>
    public bool LoadRemoteImages { get; set; }
}

public sealed class SyncSettings
{
    /// <summary>Mail content of the last N days is kept available offline.</summary>
    public int OfflineDays { get; set; } = 14;
    /// <summary>Messages larger than this are only downloaded when opened.</summary>
    public int PrefetchMaxKb { get; set; } = 2048;
    /// <summary>At most this many message headers per folder are kept locally.</summary>
    public int HeadersPerFolder { get; set; } = 5000;
    /// <summary>Polling interval for servers without IDLE and for non-inbox folders.</summary>
    public int PollMinutes { get; set; } = 5;
}

public sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppearanceSettings Appearance { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 760;

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken settings file must never stop the app; fall back to defaults.
        }
        return new();
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
