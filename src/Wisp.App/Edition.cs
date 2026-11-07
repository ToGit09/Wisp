using Wisp.Core.Smart;

namespace Wisp.App;

/// <summary>
/// Which Wisp 2 edition this build is. Decided at build time (-p:WispEdition=AI), so the light edition
/// carries no AI code. Each edition updates from its own Velopack channel ("v2-light-win", "v2-ai-linux" …),
/// which is how an installed edition stays the same edition, and why Wisp 1 never sees Wisp 2 on its own.
/// </summary>
static class Edition
{
#if WISP_AI
    public const bool IsAi = true;
#else
    public const bool IsAi = false;
#endif

    /// <summary>Major version: part of the channel name, so Wisp 3 later needs an explicit upgrade as well.</summary>
    public const int Major = 2;

    public static string Name => NameOf(IsAi);
    public static string NameOf(bool ai) => ai ? "AIEdition" : "LightEdition";

    public static string Channel(bool ai) => $"v{Major}-{(ai ? "ai" : "light")}-{Os}";

    static string Os => OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";

    /// <summary>The local model in the AI edition, null in the light edition.</summary>
    public static ILocalAi? CreateAi()
    {
#if WISP_AI
        return new Wisp.Ai.LlamaAi();
#else
        return null;
#endif
    }
}
