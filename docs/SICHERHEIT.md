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

## Bekannte Grenzen

- Wer als derselbe Benutzer am entsperrten Rechner sitzt, kann Wisp öffnen (außer mit
  Master-Passwort). Das ist bei allen Mail-Clients so.
- Während Wisp läuft, liegen geöffnete Mails entschlüsselt im Arbeitsspeicher.
