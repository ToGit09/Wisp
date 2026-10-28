# Wisp – Architektur

Wisp ist ein schlanker E-Mail-Client für Windows und Linux. Ziele, in dieser Reihenfolge:

1. **Sicher** – keine Passwörter oder Mails unverschlüsselt auf der Platte.
2. **Verlustfrei offline** – schlechte oder fehlende Verbindung führt nie zu Datenverlust,
   weder lokal noch auf dem Server.
3. **Leicht** – wenig RAM/CPU, kein Tracking, keine Werbung, keine Telemetrie.
4. **Einfach** – minimalistische Oberfläche; das Aussehen lässt sich ohne Programmierkenntnisse
   in den Einstellungen anpassen.

## Technik

| Bereich      | Wahl                                   | Warum |
|--------------|----------------------------------------|-------|
| Sprache      | C# / .NET 10 (LTS)                     | Plattformübergreifend, gute Krypto in der Standardbibliothek |
| Oberfläche   | Avalonia 12 (Fluent)                   | Echte native Fenster unter Windows und Linux, kein Browser-Unterbau |
| Mail         | MailKit / MimeKit                      | Ausgereifteste IMAP/SMTP-Bibliothek in .NET (IDLE, CONDSTORE, QRESYNC, OAuth2) |
| Speicher     | SQLite (Microsoft.Data.Sqlite)         | Eine Datei, robust bei Absturz (WAL), keine Server-Komponente |
| Verschlüsselung | AES-256-GCM (System.Security.Cryptography) | Authentifizierte Verschlüsselung, keine Fremdbibliothek |
| Schlüssel­schutz | Windows: DPAPI · Linux: Secret Service (GNOME-Schlüsselbund/KWallet) · optional: Master-Passwort (Argon2id) | |

Bewusst **nicht** enthalten: POP3 (löscht standardmäßig auf dem Server), eingebettete
Browser-Engine, Kalender/Kontakte/Chat, Plugins, automatisches Nachladen externer Bilder.

## Projektstruktur

```
src/Wisp.Core      Logik ohne UI: Konten, Anbieter, Sync, Offline-Warteschlange, Speicher, Krypto
src/Wisp.App       Avalonia-Oberfläche (MVVM)
tests/Wisp.Core.Tests   Tests, v. a. für die Sicherheitsregeln des Sync
docs/              Diese Dokumente
```

## Datenfluss

```
             ┌──────────────┐     ┌───────────────┐
  UI  ──────▶│ MailService  │────▶│ OperationQueue│──┐   (Aktionen des Nutzers,
  (liest nur │ (Fassade)    │     │ (pending_ops) │  │    dauerhaft gespeichert)
  lokal)     └──────┬───────┘     └───────────────┘  │
                    │ liest                          ▼
             ┌──────▼───────┐   schreibt     ┌──────────────┐   IMAP/SMTP   ┌────────┐
             │  MailStore   │◀───────────────│ AccountWorker│◀─────────────▶│ Server │
             │ (SQLite, AES)│                │ (je Konto)   │               └────────┘
             └──────────────┘                └──────────────┘
```

Die Oberfläche spricht **nie direkt mit dem Server**. Sie liest aus dem lokalen,
verschlüsselten Speicher und legt Aktionen in die Warteschlange. Ein Hintergrund-Worker pro
Konto gleicht Warteschlange und Server ab, sobald eine Verbindung besteht.
Dadurch fühlt sich Wisp offline genauso an wie online.

Details: [SYNC.md](SYNC.md) · [SICHERHEIT.md](SICHERHEIT.md) · [ANBIETER.md](ANBIETER.md)
