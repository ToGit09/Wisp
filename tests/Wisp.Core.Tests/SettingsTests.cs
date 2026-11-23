using Wisp.Core.Settings;

namespace Wisp.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void New_settings_use_turquoise_and_need_no_upgrade()
    {
        var s = new AppearanceSettings { Palette = AppearanceSettings.CurrentPalette };
        Assert.Equal("#0E8F8A", s.Accent);
        Assert.False(s.Upgrade());
    }

    [Fact]
    public void Wisp1_default_blue_moves_to_turquoise()
    {
        var s = new AppearanceSettings { Accent = "#4f6bed", Palette = 0 };
        Assert.True(s.Upgrade());
        Assert.Equal(AppearanceSettings.DefaultAccent, s.Accent);
        Assert.Equal(AppearanceSettings.CurrentPalette, s.Palette);
    }

    [Fact]
    public void Violet_preview_default_moves_to_turquoise()
    {
        var s = new AppearanceSettings { Accent = AppearanceSettings.PreviewAccent, Palette = 2 };
        Assert.True(s.Upgrade());
        Assert.Equal(AppearanceSettings.DefaultAccent, s.Accent);
    }

    [Fact]
    public void A_chosen_colour_stays()
    {
        // Violet chosen in Wisp 1 (palette 0) was the user's own choice, not a default.
        var violet = new AppearanceSettings { Accent = AppearanceSettings.PreviewAccent, Palette = 0 };
        var orange = new AppearanceSettings { Accent = "#D9771F", Palette = 2 };
        violet.Upgrade();
        orange.Upgrade();
        Assert.Equal(AppearanceSettings.PreviewAccent, violet.Accent);
        Assert.Equal("#D9771F", orange.Accent);
    }
}
