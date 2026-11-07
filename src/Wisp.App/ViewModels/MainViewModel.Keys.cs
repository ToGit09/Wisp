using CommunityToolkit.Mvvm.Input;

namespace Wisp.App.ViewModels;

/// <summary>Keyboard navigation of the main window (the keys themselves are read in MainWindow.axaml.cs; F1 lists them).</summary>
public sealed partial class MainViewModel
{
    /// <summary>What a left/right step did, so the view can move the keyboard along.</summary>
    public enum SideStep { None, Tab, IntoSidebar, OutOfSidebar }

    /// <summary>
    /// Left arrow (or mouse "back"). Inbox and sidebar form one row: [sidebar] [Persönlich] … [Alle].
    /// In the inbox it goes to the previous tab; from the first tab (and in every other folder) into the sidebar,
    /// which opens if it was collapsed.
    /// </summary>
    public SideStep StepLeft()
    {
        if (SelectedTabIndex is > 0 and var i)
        {
            SelectCategory(CategoryTabs[i - 1]);
            return SideStep.Tab;
        }
        SetSidebarOpen(true);
        return SideStep.IntoSidebar;
    }

    /// <summary>Right arrow (or mouse "forward"): collapses an open sidebar first, then goes to the next tab.</summary>
    public SideStep StepRight()
    {
        if (SidebarAlwaysOpen)
        {
            SetSidebarOpen(false);
            return SideStep.OutOfSidebar;
        }
        if (SelectedTabIndex is >= 0 and var i && i < CategoryTabs.Count - 1)
        {
            SelectCategory(CategoryTabs[i + 1]);
            return SideStep.Tab;
        }
        return SideStep.None;
    }

    /// <summary>Index of the chosen inbox tab, -1 when no tabs are shown.</summary>
    int SelectedTabIndex => ShowCategoryTabs ? CategoryTabs.IndexOf(CategoryTabs.First(t => t.IsSelected)) : -1;

    /// <summary>Strg+Tab / Strg+Umschalt+Tab: next or previous tab, round the end. False when no tabs are shown.</summary>
    public bool CycleCategory(int delta)
    {
        if (SelectedTabIndex is not (>= 0 and var i)) return false;
        SelectCategory(CategoryTabs[(i + delta + CategoryTabs.Count) % CategoryTabs.Count]);
        return true;
    }

    /// <summary>Keys 1–5: a tab directly. False when no tabs are shown or there is no such tab.</summary>
    public bool SelectCategoryAt(int index)
    {
        if (!ShowCategoryTabs || index < 0 || index >= CategoryTabs.Count) return false;
        SelectCategory(CategoryTabs[index]);
        return true;
    }

    /// <summary>Strg+1 … Strg+9: the n-th entry of the sidebar, from the top (0-based here).</summary>
    public bool GoToSidebarEntry(int index)
    {
        if (AllSidebar.ElementAtOrDefault(index) is not { } item) return false;
        SelectedSidebarItem = item;
        return true;
    }

    /// <summary>J: spam folder → "kein Spam", everywhere else → "als Spam markieren".</summary>
    [RelayCommand]
    void ToggleSpam()
    {
        if (QuickJunk) NotSpam();
        else MarkSpam();
    }

    /// <summary>Same switch as "Seitenleiste automatisch einklappen" in the settings, so it is remembered.</summary>
    void SetSidebarOpen(bool open)
    {
        if (SidebarAlwaysOpen == open) return;
        _mail.Settings.Appearance.AutoCollapseSidebar = !open;
        try { _mail.SaveSettings(); } catch (IOException) { }
        OnPropertyChanged(nameof(SidebarAlwaysOpen));
    }
}
