# Änderungen

## 2.0.0 – Wisp 2

Zwei Editionen: **LightEdition** und **AIEdition**. Beim ersten Start wählen Sie eine aus, wechseln lässt sie sich jederzeit unter *Einstellungen → Sortierung & KI*. Jede Edition bekommt nur ihre eigenen Updates. Wisp 1 wird nicht automatisch auf Wisp 2 aktualisiert (siehe [docs/EDITIONEN.md](docs/EDITIONEN.md)).

- Einmalcodes (Bestätigungs-, Anmelde-, TAN-Codes) aus neuen Mails werden automatisch kopiert, erscheinen nicht im Windows-Zwischenablage-Verlauf und werden nach 2 Minuten wieder entfernt
- Posteingang mit Reitern „Wichtig“, „Werbung“, „Spamverdacht“ und „Alle“; Rechtsklick → „Absender immer einordnen als …“
- Erweiterte Spamerkennung: SPF/DKIM/DMARC, gefälschte Absendernamen, abweichende Antwortadressen, Spam-Kennzeichen des Servers und ein Wortfilter, der aus Ihrem Spam-Ordner lernt. Verdächtige Mails werden markiert, nie verschoben
- AIEdition: ein lokales KI-Modell (Qwen3 1.7B) fasst neue Mails im Hintergrund zusammen und sortiert sie genauer; ältere Mails auf Knopfdruck. Läuft komplett offline auf dem eigenen Computer

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
