# Änderungen

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
