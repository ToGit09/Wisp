using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wisp.App.Views;

/// <summary>F1: every keyboard shortcut of Wisp on one page (see MainWindow.Keys.cs). Esc closes it.</summary>
public sealed class ShortcutsWindow : Window
{
    // Keys of one shortcut are joined with "+", alternatives separated by "|".
    static readonly (string Title, (string Keys, string Text)[] Rows)[] Navigation =
    [
        ("Bewegen", [
            ("← | →", "Posteingang: Reiter wechseln, vom ersten Reiter in die Seitenleiste. Andere Ordner: Seitenleiste auf- und zuklappen"),
            ("Maus zurück | Maus vor", "wie ← und →"),
            ("1 … 5", "Reiter im Posteingang direkt wählen"),
            ("Strg+Tab | Strg+Umschalt+Tab", "nächster / vorheriger Reiter"),
            ("Strg+1 … 9", "Ordner in der Seitenleiste, von oben gezählt"),
            ("↑ | ↓", "vorige / nächste Mail; in der Seitenleiste: Ordner wechseln"),
            ("Strg+↑ | Strg+↓", "vorige / nächste Mail, auch während man liest"),
            ("Enter", "aus der Seitenleiste in die Mail-Liste; in „Entwürfe“: weiterschreiben"),
            ("Leertaste", "im Lesebereich weiterblättern, am Ende die nächste ungelesene Mail; ohne offene Mail: die erste ungelesene"),
            ("Umschalt+Leertaste", "zurückblättern"),
            ("Esc", "Mail schließen"),
        ]),
        ("Befehlsfeld", [
            ("Strg+K", "Ordner, Reiter, Konten, Aktionen und Suche in einem Feld"),
            ("↑ | ↓ | Enter", "Eintrag wählen und ausführen"),
            ("Esc", "schließen"),
        ]),
        ("Suchen", [
            ("Strg+F | F3", "ins Suchfeld"),
            ("↓ | Enter", "aus dem Suchfeld in die Ergebnisse"),
            ("Esc", "Suche leeren, ein zweites Esc zurück zur Liste"),
        ]),
    ];

    static readonly (string Title, (string Keys, string Text)[] Rows)[] Actions =
    [
        ("Mails", [
            ("N | Strg+N", "neue Mail"),
            ("R | Strg+R", "antworten"),
            ("Umschalt+R | Strg+Umschalt+R", "allen antworten"),
            ("W | Strg+L", "weiterleiten"),
            ("Entf | Strg+D", "löschen"),
            ("A", "archivieren"),
            ("J", "als Spam markieren (im Spam-Ordner: kein Spam)"),
            ("S", "markieren (Stern)"),
            ("U | Strg+U", "gelesen / ungelesen"),
            ("Strg+A", "alle Mails der Liste auswählen; nach einem Klick in die Mail: ihren ganzen Text markieren"),
            ("Strg+C", "markierten Text der offenen Mail kopieren (auch per Rechtsklick)"),
            ("Strg+Z", "letzte Aktion rückgängig (Löschen, Archivieren, Verschieben, Senden …)"),
#if WISP_AI
            ("Z", "offene Mail mit der lokalen KI zusammenfassen"),
#endif
        ]),
        ("Schnellantwort", [
            ("Enter", "ins Antwortfeld unter der offenen Mail"),
            ("Strg+Enter", "senden (mit „Rückgängig“)"),
            ("Esc", "zurück zur Liste, der Text bleibt bei seiner Mail"),
            ("Rechtsklick auf Senden", "als Entwurf speichern oder später senden"),
        ]),
        ("Allgemein", [
            ("F5", "aktualisieren"),
            ("Strg+Plus | Strg+Minus | Strg+0", "Mail vergrößern / verkleinern / 100 % (im Lesebereich)"),
            ("Strg+,", "Einstellungen"),
            ("F1", "diese Übersicht"),
        ]),
        ("Neue Mail schreiben", [
            ("Strg+Enter", "senden (wenn in den Einstellungen eingeschaltet)"),
            ("Esc", "Fenster schließen, solange nichts geschrieben ist"),
        ]),
    ];

    public ShortcutsWindow()
    {
        Title = "Tastenkürzel";
        Width = 960;
        MinWidth = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Wisp/Assets/wisp.ico")));
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F1) Close();
        };

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*") };
        columns.Children.Add(Column(Navigation));
        var right = Column(Actions);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28, 22, 28, 28),
                Children =
                {
                    new TextBlock { Text = "Tastenkürzel", Classes = { "title" } },
                    new TextBlock
                    {
                        Text = "Einzelne Tasten wirken nur, solange Sie nicht gerade in ein Textfeld schreiben. Kürzel mit Strg gehen immer.",
                        Classes = { "hint" }, Margin = new Thickness(0, 4, 0, 0),
                    },
                    columns,
                },
            },
        };
    }

    static StackPanel Column((string Title, (string Keys, string Text)[] Rows)[] sections)
    {
        var panel = new StackPanel();
        foreach (var (title, rows) in sections)
        {
            panel.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
            foreach (var (keys, text) in rows)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 4) };
                row.Children.Add(Keys(keys));
                var description = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                Grid.SetColumn(description, 1);
                row.Children.Add(description);
                panel.Children.Add(row);
            }
        }
        return panel;
    }

    /// <summary>"R | Strg+R" → [R], below it [Strg]+[R]: one line per alternative, so nothing wraps mid-shortcut.</summary>
    static StackPanel Keys(string keys)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        foreach (var alternative in keys.Split(" | "))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var parts = alternative.Split('+');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) line.Children.Add(Joiner("+"));
                line.Children.Add(Cap(parts[i]));
            }
            panel.Children.Add(line);
        }
        return panel;
    }

    static TextBlock Joiner(string text) => new()
    {
        Text = text, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0),
    };

    static Border Cap(string key)
    {
        var cap = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(0, 2),
            MinWidth = 22,
            Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
        };
        cap.Bind(Border.BackgroundProperty, cap.GetResourceObservable("WispSidebar"));
        cap.Bind(Border.BorderBrushProperty, cap.GetResourceObservable("WispBorder"));
        return cap;
    }
}
