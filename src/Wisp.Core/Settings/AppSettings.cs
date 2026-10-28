using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Core.Settings;

public enum ThemeMode { System, Light, Dark }
public enum Density { Compact, Normal, Relaxed }
public enum LinkConfirmation { SuspiciousOnly, Always }

/// <summary>Look and feel. Contains no personal data, so it is stored as plain JSON.</summary>
public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "#4F6BED";
    public string Font { get; set; } = "Inter";
    public double FontSize { get; set; } = 14;
    public Density Density { get; set; } = Density.Normal;
    public bool ShowPreview { get; set; } = true;
    /// <summary>Coloured initials of the sender in the message list.</summary>
    public bool ShowAvatars { get; set; } = true;
    /// <summary>Sidebar shrinks to icons and expands while the mouse is on it. Off: always expanded.</summary>
    public bool AutoCollapseSidebar { get; set; } = true;
    /// <summary>"Wisp (3)" in the window title / taskbar.</summary>
    public bool UnreadInTitle { get; set; } = true;
    /// <summary>While a mail is open, the sidebar offers big buttons to delete, move and flag it.</summary>
    public bool SidebarMailActions { get; set; } = true;
    /// <summary>Right-click menu on mails in the list and in the reader.</summary>
    public bool ContextMenu { get; set; } = true;
    /// <summary>Zoom of HTML mails (0.9 = 90 %). Mails wider than the reader are shrunk to fit on top of this.</summary>
    public double HtmlZoom { get; set; } = 0.9;
}

public sealed class PrivacySettings
{
    /// <summary>Render HTML mails with layout instead of converting them to plain text. Off by default.</summary>
    public bool ShowHtml { get; set; }
    /// <summary>Load images from the internet automatically. Off by default (they can be used for tracking).</summary>
    public bool LoadRemoteImages { get; set; }
    /// <summary>Write crash.log (types and stack traces only) for troubleshooting.</summary>
    public bool WriteCrashLog { get; set; } = true;
}

public sealed class ReadingSettings
{
    /// <summary>Opening a mail marks it as read. Off: only the "read/unread" button does.</summary>
    public bool MarkReadOnOpen { get; set; } = true;
    /// <summary>List of all links with their real address below text mails.</summary>
    public bool ShowLinkList { get; set; } = true;
    /// <summary>Highlight links whose text shows a different address than their target.</summary>
    public bool WarnSuspiciousLinks { get; set; } = true;
    public LinkConfirmation ConfirmLinks { get; set; } = LinkConfirmation.SuspiciousOnly;
}

public sealed class ComposeSettings
{
    /// <summary>Composer starts in "Advanced" (HTML) mode. The toggle in the composer remembers the last choice.</summary>
    public bool Advanced { get; set; }
    public bool SignatureEnabled { get; set; }
    public string Signature { get; set; } = "";
    /// <summary>Replies contain the quoted original.</summary>
    public bool QuoteOnReply { get; set; } = true;
    public bool CtrlEnterSends { get; set; } = true;
    public bool ConfirmDiscard { get; set; } = true;
    /// <summary>Mails wait this long in the outbox, so "Rückgängig" can still stop them. 0 = send at once.</summary>
    public int SendDelaySeconds { get; set; } = 5;
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
    /// <summary>Keep a push connection (IMAP IDLE) open so new mail shows up instantly. Off: check every PollMinutes.</summary>
    public bool UsePush { get; set; } = true;
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
    public ReadingSettings Reading { get; set; } = new();
    public ComposeSettings Compose { get; set; } = new();
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
