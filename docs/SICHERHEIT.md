# Wisp – Sicherheit

## Schlüssel

- Beim ersten Start erzeugt Wisp einen zufälligen 256-Bit-Datenschlüssel.
- Dieser Schlüssel liegt **nie im Klartext** auf der Platte. Er wird geschützt durch:
  - **Windows:** DPAPI (an das Windows-Benutzerkonto gebunden).
  - **Linux:** Secret Service (GNOME-Schlüsselbund, KWallet, KeePassXC …).
  - **Optional / Fallback:** ein Master-Passwort. Daraus wird mit **Argon2id**
    (64 MiB, 3 Durchläufe) ein Schlüssel abgeleitet, der den Datenschlüssel verschlüsselt.
    Ohne Master-Passwort lässt sich der Speicher dann nicht öffnen – auch nicht vom selben
    Benutzerkonto. Ist unter Linux kein Schlüsselbund vorhanden, ist das Master-Passwort Pflicht.

## Was verschlüsselt wird

Alles mit AES-256-GCM (authentifiziert: Manipulation wird erkannt). Jeder Wert bekommt eine
eigene Zufalls-Nonce und ist an seinen Zweck gebunden (z. B. `message.body`), damit
verschlüsselte Werte nicht zwischen Feldern vertauscht werden können.

| Daten | Verschlüsselt |
|-------|---------------|
| Passwörter, App-Passwörter, OAuth-Tokens | ja |
| E-Mail-Adresse und Name des Kontos | ja |
| Ordnernamen | ja (für Suchen wird ein HMAC-Hash genutzt) |
| Betreff, Absender, Empfänger, Vorschau | ja |
| Kompletter Mailinhalt inkl. Anhängen | ja |
| Postausgang und Entwürfe | ja |
| Message-ID | nur als HMAC-Hash |
| KI-Zusammenfassungen (AIEdition) | ja |
| Absender-Regeln („immer einordnen als …“) | Absender nur als HMAC-Hash |
| Kategorie (Wichtig/Werbung/Spamverdacht) und Kopfzeilen-Merkmale (z. B. „Newsletter“, „SPF fehlgeschlagen“) | nein – kleine Zahlen wie die Flags, für die Reiter nötig |
| Datum, Größe, Flags (gelesen/Stern), UIDs | nein – für Sortierung nötig, enthält keinen Inhalt |
| Darstellungs-Einstellungen | nein – enthalten keine persönlichen Daten |

SQLite läuft mit `secure_delete=ON`: gelöschte Daten werden in der Datei überschrieben.
Unter Linux werden Daten-Verzeichnisse mit Rechten `700` angelegt.

## Netzwerk

- Nur verschlüsselte Verbindungen (TLS bzw. STARTTLS-Pflicht). Unverschlüsselte Verbindungen
  sind nicht möglich, ungültige Zertifikate werden abgelehnt.
- OAuth2 mit PKCE über den System-Browser, wo Anbieter es verlangen (Microsoft) oder anbieten.
  Wisp sieht dabei Ihr Passwort nie.
- Keine Telemetrie, keine Update-Abfragen im Hintergrund, keine Werbung.

## Mails anzeigen

- Zwei Einstellungen unter **Einstellungen → Datenschutz**, beide standardmäßig **aus**:
  - **HTML-Mails als HTML anzeigen.** Aus: Wisp wandelt HTML in formatierten Text um.
    An: Darstellung mit Layout über eine reine .NET-Engine (Avalonia.HtmlRenderer), die
    **kein JavaScript ausführen kann**. Vorher entfernt Wisp Skripte, Frames, Formulare,
    Event-Handler, externe Stylesheets und gefährliche Links (`javascript:`, `file:` …).
  - **Externe Bilder automatisch laden.** Aus: Bilder aus dem Internet (und damit
    Tracking-Pixel) werden nicht abgerufen; niemand erfährt, ob und wann Sie eine Mail
    geöffnet haben. Eingebettete Bilder (`cid:`) werden immer angezeigt, da sie in der Mail
    selbst stecken.
- Beides lässt sich pro Mail einmalig einschalten („Originaldarstellung“, „Bilder laden“),
  ohne die Einstellung zu ändern.
- Werden Bilder geladen, dann ohne Cookies, ohne Referrer, max. 5 MB, nur `image/*`, und nie
  aus dem lokalen Netzwerk (Schutz vor Router-/Intranet-Abfragen durch präparierte Mails).
- Links werden mit ihrer **echten Zieladresse** angezeigt. Ein Link, dessen Text eine andere
  Domain zeigt als das Ziel, wird als verdächtig markiert (Phishing-Schutz).
- Anhänge werden nur auf ausdrücklichen Wunsch an einem selbst gewählten Ort gespeichert.
  Wisp öffnet Anhänge nicht automatisch.

## Fehlerprotokoll

Bei einem Programmfehler schreibt Wisp `crash.log` in den Datenordner. Es enthält nur
Fehlertyp und Stacktrace, keine Fehlermeldungen, Adressen oder Mailinhalte.

## Bekannte Grenzen

- Wer als derselbe Benutzer am entsperrten Rechner sitzt, kann Wisp öffnen (außer mit
  Master-Passwort). Das ist bei allen Mail-Clients so.
- Während Wisp läuft, liegen geöffnete Mails entschlüsselt im Arbeitsspeicher.

## Wisp 2: Sortierung, Einmalcodes und lokale KI

- **Alles lokal.** Kategorien, Spamerkennung, Einmalcodes und die KI arbeiten nur auf diesem Computer.
  Keine Mail, kein Code und keine Zusammenfassung wird irgendwohin gesendet.
- **Wortfilter.** Die Spamerkennung lernt aus Betreff, Absender und Vorschau der Mails in Ihren Spam-Ordnern.
  Die Wortzählungen liegen nur im Arbeitsspeicher und werden nie auf die Platte geschrieben.
- **Einmalcodes** werden nur aus neuen (höchstens 10 Minuten alten), ungelesenen Mails im Posteingang kopiert.
  Unter Windows bekommt der Code die Kennzeichen `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory = 0` und `CanUploadToCloudClipboard = 0`: Er landet weder im
  Zwischenablage-Verlauf (Win+V) noch in der Cloud-Zwischenablage. Nach einstellbarer Zeit (Standard 2 Minuten)
  leert Wisp die Zwischenablage wieder – aber nur, wenn dort noch der Code steht.
- **KI-Modell (AIEdition).** Einmaliger Download von Hugging Face über HTTPS. Datei und Version sind fest
  im Programm hinterlegt (Commit `d7f544e…`) und werden per **SHA-256** geprüft; eine veränderte Datei wird
  gelöscht und nicht geladen. Das Modell ist eine reine Gewichtsdatei (GGUF), kein ausführbarer Code.
  Es liegt unverschlüsselt unter `models/` im Datenordner, da es öffentlich ist und nichts Persönliches enthält.
  Das Modell sieht Absender, Betreff und Text einer Mail (ohne zitierte Antworten, gekürzt), nie Anhänge.
