# Änderungen

## 1.0.4 – Mails schließen, Text kopieren, Fehlerbehebungen

Fehlerbehebungen und Verbesserungen aus Wisp 2, für alle, die bei Wisp 1 bleiben.

- Eine geöffnete Mail lässt sich schließen: Knopf „✕“ rechts oben im Lesebereich oder Esc. Danach ist keine Mail mehr ausgewählt und der Lesebereich ist leer. Esc beendet wie bisher auch den Auswahlmodus
- **Mailtext markieren und kopieren**: Text lässt sich mit der Maus markieren – über mehrere Absätze hinweg, Doppelklick nimmt ein Wort, Dreifachklick einen Absatz, Umschalt+Klick erweitert. Strg+C kopiert, Strg+A markiert nach einem Klick in die Mail ihren ganzen Text; per Rechtsklick gibt es „Kopieren“, „Alles markieren“ und auf Links „Link-Adresse kopieren“. Links im Text der Textansicht lassen sich direkt anklicken
- Nach dem Löschen (ebenso Archivieren, Verschieben, Spam, Wiederherstellen) wird gleich die nächste Mail der Liste ausgewählt und geöffnet
- Esc schließt die Einstellungen und ein noch leeres Fenster „Neue Mail“ (mit Text bleibt es offen)
- Das App-Icon in der Titelleiste ist bei 125 %, 150 % und 175 % Skalierung nicht mehr unscharf
- Schneller: Die Mail-Liste entschlüsselt die Kopfzeilen jeder Mail nur noch einmal statt bei jeder Aktualisierung (spürbar beim Suchen und in großen Ordnern); HTML-Mails ohne Textteil werden beim Öffnen einmal weniger durchlaufen; externe Bilder einer Mail werden nicht mehr weitergeladen, wenn man schon die nächste liest
- HTML-Mails sehen deutlich mehr so aus wie gedacht:
  - Knöpfe und farbige Flächen, die ihre Farbe über `background:` setzen, waren unsichtbar (weiße Schrift auf Weiß) – jetzt mit Farbe, auch bei Farbverläufen; runde Ecken werden gezeichnet
  - Der versteckte Vorschautext am Anfang von Newslettern („Preheader“) erscheint nicht mehr als erste Zeile – auch nicht in der Textansicht
  - Schrift, Textfarbe und Hintergrundfarbe, die eine Mail für die ganze Seite festlegt, gingen verloren; der Hintergrund färbt jetzt das ganze Blatt
  - Fotos ohne Größenangabe machten die Mail tausende Pixel breit und damit winzig – sie werden auf Blattbreite eingepasst
  - Zitate bekommen eine ruhige Linie am Rand, Code eine Festbreitenschrift mit erhaltenen Zeilenumbrüchen; Inhalte mit `margin: auto` stehen mittig
  - Zählpixel (1×1-Bilder zur Lesebestätigung) werden entfernt und nie geladen, auch nicht mit „Bilder laden“

## 1.0.3 – Weg zu Wisp 2

- Unter *Einstellungen → Über Wisp* erscheint ein Knopf „Auf Wisp 2 upgraden“, sobald Wisp 2 auf GitHub veröffentlicht ist (einmaliger, dezenter Hinweis beim Start). Wisp 1 aktualisiert sich nie von selbst auf Wisp 2

## 1.0.2 – Fehlerbehebungen

- HTML-Mails (z. B. Netflix): Bilder ohne `alt`-Wert verloren ihre Breite und sprengten das Layout; Spalten fester Breite werden jetzt zentriert, Hintergrundbilder füllen ihre Zelle, Titel werden nicht mehr abgeschnitten
- HTML-Mails: Regeln für Handy-Bildschirme (`@media`) werden am Desktop nicht mehr angewendet
- Zoom-Leiste: die Prozentanzeige setzt selbst auf 100 % zurück, statt eines zweiten „100 %“-Knopfs

## 1.0.1 – Fehlerbehebungen

- Kontowechsel: Die Seitenleiste zeigt nur noch ein Konto; gewechselt wird über den Umschalter oben (Punkt bei neuen Mails in anderen Konten)
- HTML-Mails: halbtransparente Textfarben (z. B. bei Google-Mails) verschwanden – jetzt sichtbar; außerdem hsl(), #RRGGBBAA und Prozentwerte
- HTML-Mails: Die Zoom-Leiste überdeckt den Mailtext nicht mehr
- Auswahlmodus: Doppelklick auf eine Mail, danach weitere Mails per Klick hinzufügen und gemeinsam löschen
- Kompaktere Kopfzeile im Leser; Doppelklick markiert eine ganze E-Mail-Adresse

## 1.0.0 – erste Veröffentlichung

- IMAP/SMTP-Client für Windows und Linux, eine IMAP-Verbindung pro Konto
- Fertige Einstellungen für Gmail, Outlook.com, Yahoo, iCloud, GMX, WEB.DE, T-Online, Posteo, mailbox.org, AOL, freenet
- Passwörter, Mails, Anhänge und Ordnernamen AES-256-verschlüsselt; Schlüssel über DPAPI, Linux-Schlüsselbund oder Master-Passwort
- Offline-fest: Aktionen werden lokal gespeichert und später übertragen
- HTML-Darstellung und externe Bilder standardmäßig aus, Warnung bei verdächtigen Links
- Editor „Light“ und „Advanced“, Signatur, Senden mit „Rückgängig“
- Darstellung anpassbar: Farbschema, Akzentfarbe, Schrift, Größe, Abstände
- Schnellaktionen je nach Ordner, Rechtsklick-Menü, Spam markieren
- Installation per Setup (Windows) oder AppImage (Linux), automatische Updates über GitHub (abschaltbar)
