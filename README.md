# Wisp

Ein leichter, sicherer E-Mail-Client für Windows und Linux.

- **Leicht:** keine Werbung, keine Telemetrie, kein eingebetteter Browser. Eine IMAP-Verbindung pro Konto.
- **Sicher:** Passwörter, Mails, Anhänge und Ordnernamen liegen nur AES-256-verschlüsselt auf der Platte.
  Der Schlüssel ist durch Windows (DPAPI), den Linux-Schlüsselbund oder ein optionales Master-Passwort geschützt.
- **Offline-fest:** Alles, was Sie tun, wird zuerst lokal gespeichert und übertragen, sobald es wieder Netz gibt.
  Wisp löscht lokal nur, was der Server ausdrücklich als gelöscht bestätigt – siehe [docs/SYNC.md](docs/SYNC.md).
- **Einfach anzupassen:** Farbschema, Akzentfarbe, Schrift, Schriftgröße und Abstände unter *Einstellungen → Darstellung*,
  live und ohne Programmierkenntnisse.
- **Datenschutz:** HTML-Darstellung und externe Bilder sind standardmäßig aus und lassen sich in den
  Einstellungen oder pro Mail einschalten. Verdächtige Links (Text zeigt andere Domain als das Ziel) werden markiert.
- **Schreiben:** Editor „Light“ (Text) oder „Advanced“ (HTML mit Formatierungsleiste und Vorschau), Signatur,
  Senden mit „Rückgängig“.
- **Abschaltbar:** Fast alles lässt sich in den Einstellungen an- und ausschalten – Seitenleiste mit
  Auto-Einklappen, Mail-Aktionen in der Seitenleiste, Rechtsklick-Menü, Absender-Kürzel, Push, einzelne Ordner u. v. m.

Unterstützt Gmail, Outlook.com, Yahoo, iCloud, GMX, WEB.DE, T-Online, Posteo, mailbox.org, AOL, freenet
und jeden anderen IMAP/SMTP-Anbieter – siehe [docs/ANBIETER.md](docs/ANBIETER.md).

## Bauen und starten

Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Wisp.slnx
dotnet test Wisp.slnx
dotnet run --project src/Wisp.App
```

Eigenständige Pakete (ohne installiertes .NET):

```bash
dotnet publish src/Wisp.App -c Release -r win-x64 --self-contained -o publish/win-x64
dotnet publish src/Wisp.App -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

Zum Ausprobieren ohne das eigene Profil: Umgebungsvariable `WISP_HOME` auf einen leeren Ordner setzen.

**Windows-Hinweis:** Ist *Smart App Control* aktiv, blockiert Windows selbst gebaute, unsignierte Programme
oft („Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert“). Für eine Veröffentlichung muss Wisp
mit einem Code-Signing-Zertifikat signiert werden.

## Aufbau

| Ordner | Inhalt |
|--------|--------|
| `src/Wisp.Core` | Konten & Anbieter, Verschlüsselung, lokaler Speicher, Sync, Offline-Warteschlange, Postausgang, Mail-Darstellung |
| `src/Wisp.App` | Oberfläche (Avalonia) |
| `tests/Wisp.Core.Tests` | Tests, vor allem für die Sicherheits- und Löschregeln |
| `docs/` | [Architektur](docs/ARCHITEKTUR.md) · [Sync & Offline](docs/SYNC.md) · [Sicherheit](docs/SICHERHEIT.md) · [Anbieter](docs/ANBIETER.md) |

## Release veröffentlichen

1. Version in `Directory.Build.props` und `CHANGELOG.md` eintragen, committen, nach `main` bringen.
2. Tag setzen und pushen:
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```
3. Der Workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) testet, baut und packt Wisp mit
   [Velopack](https://velopack.io) und legt einen **Entwurf** unter *Releases* an:
   - `Wisp-<Version>-Windows-Setup.exe`: Installation ohne Admin-Rechte, mit Startmenü- und Desktop-Verknüpfung
   - `Wisp-<Version>-Windows-Portable.zip`: zum Entpacken, aktualisiert sich ebenfalls selbst
   - `Wisp-<Version>-Linux.AppImage`: eine Datei, ausführbar machen und starten
   - `*.nupkg`, `releases.*.json`, `RELEASES*`: das Update-Feed. **Nicht löschen**, sonst finden installierte
     Wisp-Versionen das Update nicht.

   Den Entwurf prüfen und auf *Publish release* klicken. Erst dann sehen installierte Wisp-Versionen das Update
   und laden es im Hintergrund. Installiert wird es beim nächsten Start.

Die Paket-ID ist bewusst `WispMail`, nicht `Wisp`: Velopack installiert nach `%LocalAppData%\WispMail` und löscht
diesen Ordner beim Deinstallieren. Wisps Mails und Tresor liegen in `%LocalAppData%\Wisp` und bleiben so erhalten.
**Die Paket-ID nie ändern**, sonst bekommen bestehende Installationen keine Updates mehr.

## Lizenz

Wisp ist freie Software unter der [GNU General Public License v3.0](LICENSE).
Verwendete Bibliotheken und ihre Lizenzen: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
