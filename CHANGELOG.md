# Änderungen

## 2.0.0 – Wisp 2

Zwei Editionen: **LightEdition** und **AIEdition**. Beim ersten Start wählen Sie eine aus, wechseln lässt sie sich jederzeit unter *Einstellungen → Sortierung & KI*. Jede Edition bekommt nur ihre eigenen Updates. Wisp 1 wird nicht automatisch auf Wisp 2 aktualisiert (siehe [docs/EDITIONEN.md](docs/EDITIONEN.md)).

- Einmalcodes (Bestätigungs-, Anmelde-, TAN-Codes) aus neuen Mails werden automatisch kopiert, erscheinen nicht im Windows-Zwischenablage-Verlauf und werden nach 2 Minuten wieder entfernt
- Posteingang mit Reitern „Persönlich“, „Rechnungen“, „Werbung“, „Spamverdacht“ und „Alle“; Rechtsklick → „Absender immer einordnen als …“
- Erweiterte Spamerkennung: SPF/DKIM/DMARC, gefälschte Absendernamen, abweichende Antwortadressen, Spam-Kennzeichen des Servers und ein Wortfilter, der aus Ihrem Spam-Ordner lernt. Verdächtige Mails werden markiert, nie verschoben
- AIEdition: ein lokales KI-Modell (Qwen3 1.7B) fasst neue Mails im Hintergrund zusammen und sortiert sie genauer; ältere Mails auf Knopfdruck. Läuft komplett offline auf dem eigenen Computer
- AIEdition: eigenes App-Icon mit einem kleinen Funkeln, damit man die Editionen in Taskleiste und Startmenü unterscheiden kann
- Nach dem Löschen (ebenso Archivieren, Verschieben, Spam) wird gleich die nächste Mail der Liste ausgewählt und geöffnet
- Eine geöffnete Mail lässt sich schließen: Knopf „✕“ rechts oben im Lesebereich oder Esc. Danach ist keine Mail mehr ausgewählt und der Lesebereich ist leer. Esc beendet wie bisher auch den Auswahlmodus (wie in Wisp 1.0.4)
- Pfeiltasten ←/→ (oder die Zurück/Vor-Tasten der Maus): im Posteingang durch die Reiter, vom ersten Reiter weiter in die Seitenleiste; in allen anderen Ordnern wird die Seitenleiste auf- und zugeklappt. In der aufgeklappten Seitenleiste wechseln ↑/↓ den Ordner
- Viele Tastenkürzel, Übersicht mit F1: Strg+F/F3 Suche (Esc leert sie, ↓ springt in die Ergebnisse), 1–5 und Strg+Tab für die Reiter, Strg+1…9 für die Ordner, Strg+↑/↓ nächste Mail auch beim Lesen, Leertaste blättert weiter und öffnet am Ende die nächste Mail, Enter setzt Entwürfe fort, A/J/S/U/R/W/N/Entf für Archivieren, Spam, Markieren, Gelesen, Antworten, Weiterleiten, Neue Mail und Löschen, Strg+, Einstellungen. Esc schließt außerdem die Einstellungen und ein noch leeres Fenster „Neue Mail“
- Überarbeitete Oberfläche (Begründung und Bewertung der Ideen in [docs/UI-V2.md](docs/UI-V2.md)):
  - **Schnellantwort** unter jeder Mail: Enter springt hinein, Strg+Enter sendet (mit „Rückgängig“), ein Klick übernimmt den Text ins große Fenster. Was man tippt, bleibt bei seiner Mail, auch wenn man zwischendurch eine andere öffnet
  - Rechtsklick auf den Senden-Knopf: Kapseln fahren heraus – **Speichern** (als Entwurf in den Ordner „Entwürfe“, auch offline) und **Später senden** (in einer Stunde, heute Abend, morgen früh, vor dem Wochenende: Montag früh). Geplante Mails warten verschlüsselt im Postausgang, „Jetzt senden“ und „Verwerfen“ gehen bis zuletzt; „Rückgängig“ legt den Text zurück ins Antwortfeld
  - Rechtsklick-Menü neu: eine ruhige Karte mit runden Ecken, die Zeile unter der Maus leuchtet in der Akzentfarbe (Löschen in Rot), rechts stehen die echten Tastenkürzel (R, ⇧ R, W, U, S, A, J, Entf). Was hier nicht geht, fehlt, statt ausgegraut zu sein. Untermenüs und die kleinen Menüs an Knöpfen sehen gleich aus
  - **Befehlsfeld Strg+K**: Ordner, Reiter, Konten, Aktionen für die offene Mail, „Verschieben nach …“ und Suche in einem Feld – mit dem passenden Tastenkürzel daneben
  - „Rückgängig“ jetzt auch nach Archivieren und Verschieben, und **Strg+Z** für das jeweils letzte „Rückgängig“. Der Hinweis bleibt stehen, solange die Maus darauf ist
  - Lesebereich: Betreff, Absender (mit Initialen) und Datum erscheinen sofort, der Inhalt blendet weich ein; „Wird geladen …“ erst, wenn es wirklich dauert. Stern und Gelesen-Knopf zeigen ihren Zustand
  - Leerer Lesebereich zeigt, wie viele Mails ungelesen sind; die Leertaste öffnet die nächste ungelesene (auch am Ende einer Mail). Unter dem Ordnertitel steht die Zahl der ungelesenen Mails
  - Die Auswahl der Reiter gleitet zum neuen Reiter, Knöpfe geben beim Klicken leicht nach, Hinweise gleiten von unten herein
  - „Neue Mail“ im Stil von Apple Mail: ruhige Zeilen statt Kästen, Senden oben rechts
  - AIEdition: die Zusammenfassung erscheint in der Liste anstelle der ersten Textzeile; Taste Z fasst die offene Mail zusammen, ein Funke pulsiert während des Schreibens; der Modell-Download ist ein schmaler Fortschrittsbalken
  - AIEdition: eine Zusammenfassung läuft weiter, wenn man inzwischen eine andere Mail öffnet, und wird danach wie alle Zusammenfassungen verschlüsselt gespeichert (vorher wurde sie abgebrochen und war verloren). In der Liste pulsiert der Funke, bis sie fertig ist; eine zweite wartet, bis die erste fertig ist. Die Karte flackert beim Schreiben nicht mehr
- **Mailtext markieren und kopieren**: in empfangenen und gesendeten Mails lässt sich Text mit der Maus markieren – über mehrere Absätze hinweg, Doppelklick nimmt ein Wort, Dreifachklick einen Absatz, Umschalt+Klick erweitert. Strg+C kopiert, Strg+A markiert nach einem Klick in die Mail ihren ganzen Text; per Rechtsklick gibt es „Kopieren“, „Alles markieren“ und auf Links „Link-Adresse kopieren“. Links im Text der Textansicht lassen sich jetzt direkt anklicken
- HTML-Mails sehen deutlich mehr so aus wie gedacht:
  - Knöpfe und farbige Flächen, die ihre Farbe über `background:` setzen, waren unsichtbar (weiße Schrift auf Weiß) – jetzt mit Farbe, auch bei Farbverläufen; runde Ecken werden gezeichnet
  - Der versteckte Vorschautext am Anfang von Newslettern („Preheader“) erscheint nicht mehr als erste Zeile – auch nicht in der Textansicht
  - Schrift, Textfarbe und Hintergrundfarbe, die eine Mail für die ganze Seite festlegt, gingen verloren; der Hintergrund färbt jetzt das ganze Blatt
  - Fotos ohne Größenangabe machten die Mail tausende Pixel breit und damit winzig – sie werden auf Blattbreite eingepasst
  - Zitate bekommen eine ruhige Linie am Rand, Code eine Festbreitenschrift mit erhaltenen Zeilenumbrüchen; Inhalte mit `margin: auto` stehen mittig
  - Zählpixel (1×1-Bilder zur Lesebestätigung) werden entfernt und nie geladen, auch nicht mit „Bilder laden“

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
