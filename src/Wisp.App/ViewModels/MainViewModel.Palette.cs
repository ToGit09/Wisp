using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wisp.App.ViewModels;

/// <summary>An entry of the command field: a folder, an action, an account, a search.</summary>
/// <param name="Keys">The shortcut for the same thing, shown on the right – so the field teaches the keys along the way.</param>
/// <param name="Primary">Offered before anything is typed (the things most likely wanted right now).</param>
public sealed record PaletteEntry(string Title, string? Detail, string? Keys, Geometry? Icon, Action Run, bool Primary = false)
{
    public bool HasDetail => Detail is not null;
    public bool HasKeys => Keys is not null;
}

/// <summary>
/// Strg+K: one field for everything – go to a folder, tab or account, act on the open mail, move it, search.
/// Nobody has to remember a shortcut or hunt through menus; typing a few letters is enough.
/// </summary>
public sealed partial class MainViewModel
{
    const int PaletteMax = 9;

    [ObservableProperty] public partial bool IsPaletteOpen { get; set; }
    [ObservableProperty] public partial string PaletteQuery { get; set; } = "";
    [ObservableProperty] public partial PaletteEntry? SelectedPaletteEntry { get; set; }
    public ObservableCollection<PaletteEntry> PaletteResults { get; } = [];

    public string PaletteHint => SelectedSidebarItem is { Kind: SidebarKind.Folder, Name: var name }
        ? $"Ordner, Aktion – oder in „{name}“ suchen …"
        : "Ordner oder Aktion …";

    /// <summary>F1 lives in the view; the command field asks for it.</summary>
    public event Action? ShortcutsRequested;

    [RelayCommand]
    public void OpenPalette()
    {
        PaletteQuery = "";
        UpdatePalette();
        IsPaletteOpen = true;
    }

    public void ClosePalette() => IsPaletteOpen = false;

    partial void OnPaletteQueryChanged(string value) => UpdatePalette();

    /// <summary>Moves the highlighted entry (↑/↓), wrapping round.</summary>
    public void StepPalette(int delta)
    {
        if (PaletteResults.Count == 0) return;
        var index = SelectedPaletteEntry is { } e ? PaletteResults.IndexOf(e) : -1;
        SelectedPaletteEntry = PaletteResults[((index + delta) % PaletteResults.Count + PaletteResults.Count) % PaletteResults.Count];
    }

    public void RunPalette(PaletteEntry? entry = null)
    {
        entry ??= SelectedPaletteEntry;
        if (entry is null) return;
        IsPaletteOpen = false;
        entry.Run();
    }

    void UpdatePalette()
    {
        var query = PaletteQuery.Trim();
        var all = PaletteEntries().ToList();
        List<PaletteEntry> results;
        if (query.Length == 0)
        {
            results = all.Where(e => e.Primary).Take(PaletteMax).ToList();
        }
        else
        {
            results = all
                .Select((entry, order) => (entry, order, score: Score(entry, query)))
                .Where(x => x.score >= 0)
                .OrderBy(x => x.score).ThenBy(x => x.order)
                .Select(x => x.entry)
                .Take(PaletteMax)
                .ToList();
            // Searching is always possible: last when something else matches, first when nothing does.
            if (SelectedSidebarItem is { Kind: SidebarKind.Folder } folder && !IsOutboxSelected)
            {
                var search = new PaletteEntry($"Suchen: „{query}“", $"in {folder.Name}", "Strg+F", SidebarItem.Find("IconSearch"),
                    () => SearchText = query);
                if (results.Count == 0) results.Add(search);
                else
                {
                    if (results.Count == PaletteMax) results.RemoveAt(results.Count - 1);
                    results.Add(search);
                }
            }
        }

        PaletteResults.Clear();
        foreach (var entry in results) PaletteResults.Add(entry);
        SelectedPaletteEntry = PaletteResults.FirstOrDefault();
    }

    /// <summary>Lower is better; -1 = no match. Word starts count more than hits in the middle of a word.</summary>
    static int Score(PaletteEntry entry, string query)
    {
        var title = entry.Title;
        if (title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        var words = title.Split([' ', '„', '“', ':', '/', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))) return 1;
        if (title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 2;
        if (entry.Detail?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) return 3;
        return -1;
    }

    IEnumerable<PaletteEntry> PaletteEntries()
    {
        static Geometry? Icon(string key) => SidebarItem.Find(key);
        var hasMail = Targets.Count > 0;

        if (CanUndo) yield return new("Rückgängig", Notice, "Strg+Z", Icon("IconUndo"), () => Undo(), true);

        // The open mail first: these are what one most likely wants while reading.
        if (hasMail)
        {
            if (QuickDrafts) yield return new("Weiter bearbeiten", null, "Enter", Icon("IconCompose"), () => EditDraftCommand.Execute(null), true);
            if (QuickTrash) yield return new("Wiederherstellen", null, null, Icon("IconUndo"), () => RestoreCommand.Execute(null), true);
            if (CurrentMessageId is not null && !QuickDrafts)
            {
                yield return new("Antworten", null, "R", Icon("IconReply"), () => ReplyCommand.Execute(null), true);
                yield return new("Allen antworten", null, "Umschalt+R", Icon("IconReplyAll"), () => ReplyAllCommand.Execute(null));
                yield return new("Weiterleiten", null, "W", Icon("IconForward"), () => ForwardCommand.Execute(null));
            }
            if (CanArchive) yield return new("Archivieren", null, "A", Icon("IconArchive"), () => ArchiveCommand.Execute(null), true);
            yield return new("Löschen", null, "Entf", Icon("IconTrash"), () => DeleteCommand.Execute(null), true);
            yield return new(CurrentIsUnread ? "Als gelesen markieren" : "Als ungelesen markieren", null, "U", Icon("IconMail"),
                () => ToggleReadCommand.Execute(null));
            yield return new(CurrentIsFlagged ? "Markierung entfernen" : "Markieren", null, "S", Icon("IconStar"),
                () => ToggleFlagCommand.Execute(null));
            if (QuickJunk) yield return new("Kein Spam", "zurück in den Posteingang", "J", Icon("IconInbox"), () => NotSpamCommand.Execute(null));
            else if (CanMarkSpam) yield return new("Als Spam markieren", null, "J", Icon("IconJunk"), () => MarkSpamCommand.Execute(null));
            yield return new(Targets.Count > 1 ? $"{Targets.Count} Mails exportieren" : "Mail exportieren", "als Datei speichern (.eml, .html, .txt)", "Strg+S",
                Icon("IconExport"), () => ExportCommand.Execute(null));
            if (CanSummarize) yield return new("Zusammenfassen", "lokal, mit der KI", "Z", Icon("IconSpark"), () => SummarizeCommand.Execute(null), true);
            foreach (var target in MoveTargets.ToList())
                yield return new($"Verschieben nach {target.Name}", null, null, target.Icon ?? Icon("IconMove"), () => MoveToCommand.Execute(target));
            if (ShowSortMenu && CurrentMessageId is not null)
            {
                foreach (var tab in CategoryTabs.Where(t => t.Category is not null))
                    yield return new($"Absender immer unter {tab.Label}", "Regel für alle Mails dieses Absenders", null, tab.Icon,
                        () => SortSenderCommand.Execute(tab.Category.ToString()));
            }
        }

        yield return new("Neue Mail", null, "N", Icon("IconCompose"), () => ComposeCommand.Execute(null), true);

        // Places
        var index = 0;
        foreach (var item in AllSidebar.ToList())
        {
            var keys = index < 9 ? $"Strg+{index + 1}" : null;
            index++;
            if (item == SelectedSidebarItem) continue;
            var target = item;
            yield return new(target.Name, target.HasUnread ? $"{target.Unread} ungelesen" : "Ordner", keys,
                target.Icon, () => SelectedSidebarItem = target, !hasMail);
        }
        if (ShowCategoryTabs)
        {
            for (var i = 0; i < CategoryTabs.Count; i++)
            {
                var tab = CategoryTabs[i];
                if (tab.IsSelected) continue;
                yield return new($"Reiter {tab.Label}", tab.HasUnread ? $"{tab.Unread} ungelesen" : null, $"{i + 1}", tab.Icon,
                    () => SelectCategory(tab));
            }
        }
        foreach (var account in Accounts.Where(a => !a.IsActive).ToList())
            yield return new($"Konto {account.Email}", account.HasUnread ? $"{account.Unread} ungelesen" : "Konto wechseln", null,
                Icon("IconPerson"), () => SwitchAccountCommand.Execute(account));

        // App
        yield return new("Aktualisieren", "Mails abrufen", "F5", Icon("IconRefresh"), () => RefreshCommand.Execute(null));
        yield return new(SidebarAlwaysOpen ? "Seitenleiste einklappen" : "Seitenleiste ausklappen", null, "← | →", Icon("IconAll"),
            () => SetSidebarOpen(!SidebarAlwaysOpen));
        yield return new("Einstellungen", null, "Strg+,", Icon("IconSettings"), () => OpenSettingsCommand.Execute(null));
        yield return new("Tastenkürzel", "alle auf einen Blick", "F1", Icon("IconKeyboard"), () => ShortcutsRequested?.Invoke());
        yield return new("Konto hinzufügen", null, null, Icon("IconPlus"), () => AddAccountCommand.Execute(null));
    }
}
