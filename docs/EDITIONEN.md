# Wisp 2 – Editionen, Update-Kanäle und Upgrade von Wisp 1

## Die zwei Editionen

| | LightEdition | AIEdition |
|---|---|---|
| Einmalcodes automatisch kopieren | ✓ | ✓ |
| Reiter „Wichtig / Werbung / Spamverdacht / Alle“ | ✓ (Regeln) | ✓ (Regeln + KI für neue Mails) |
| Erweiterte Spamerkennung (SPF/DKIM/DMARC, gefälschte Namen, lernender Wortfilter) | ✓ | ✓ |
| KI-Zusammenfassung | – | ✓ neue Mails im Hintergrund, ältere auf Knopfdruck |
| Größe | wie Wisp 1 | + ca. 100 MB Laufzeit, + 1,1 GB Modell (einmaliger Download) |

Die Edition wird beim Bauen festgelegt (`-p:WispEdition=AI`). Die LightEdition enthält keinerlei KI-Code
oder native KI-Bibliotheken; das Projekt `src/Wisp.Ai` wird nur in die AIEdition eingebunden.

## Erste Installation

Auf GitHub gibt es **ein** Setup (`Wisp-2.x.y-Windows-Setup.exe`, `…-Linux.AppImage`). Es installiert die
LightEdition. Beim ersten Start fragt Wisp: **LightEdition oder AIEdition?**

- *Light:* Wisp startet sofort.
- *AI:* Wisp lädt die AIEdition aus ihrem Update-Kanal (gleiche Version), startet neu und lädt danach einmalig
  das KI-Modell. Gelingt der Download nicht (offline), startet Wisp als LightEdition; der Wechsel ist jederzeit
  unter *Einstellungen → Sortierung & KI* möglich.

Das Velopack-Setup selbst kann keinen Auswahldialog zeigen – deshalb die Frage beim ersten Start.

## Update-Kanäle

| Kanal | Inhalt |
|-------|--------|
| `win`, `linux` | Wisp 1.x (Standardkanäle von Velopack) |
| `v2-light-win`, `v2-light-linux` | Wisp 2 LightEdition |
| `v2-ai-win`, `v2-ai-linux` | Wisp 2 AIEdition |

- Jede Edition fragt ausdrücklich nach ihrem eigenen Kanal (`Updater.cs`). Eine installierte LightEdition
  bekommt nur Light-Updates, eine AIEdition nur AI-Updates.
- **Wisp 1 wird nie automatisch auf Wisp 2 aktualisiert:** Wisp-2-Releases enthalten keinen Feed für die
  Kanäle `win`/`linux`. Wisp 1 sieht Wisp 2 schlicht nicht.
- Die Hauptversion steckt im Kanalnamen. Wisp 3 bekommt später `v3-…` und braucht damit ebenfalls einen
  bewussten Upgrade-Schritt.

## Upgrade von Wisp 1

Ab Wisp 1.0.3 gibt es unter *Einstellungen → Über Wisp* einen unaufdringlichen Knopf **„Auf Wisp 2 upgraden“**.
Er erscheint nur, wenn auf GitHub bereits ein Wisp 2 veröffentlicht ist. Er lädt die Wisp-2-LightEdition
(Kanalwechsel in Velopack) und startet neu; beim ersten Start von Wisp 2 folgt die Frage nach der Edition.
Wisp-1-Nutzer mit 1.0.0–1.0.2 bekommen 1.0.3 vorher ganz normal als Update.

Mails, Konten und Einstellungen bleiben beim Upgrade und beim Editionswechsel erhalten; die Datenbank wird beim
ersten Start von Wisp 2 einmalig erweitert (Schema 3). Ein Zurück auf Wisp 1 ist nicht vorgesehen.

## Branches und Releases

- `main` – Wisp 2. Tags `v2.x.y` bauen beide Editionen für beide Systeme (`.github/workflows/release.yml`).
- `release/v1` – Fehlerbehebungen für Wisp 1. Tags `v1.x.y` werden dort gesetzt und nutzen die Workflow-Datei
  dieses Branches (Standardkanäle).

Hinweis: Velopack sucht in den neuesten GitHub-Releases nach dem Feed seines Kanals. Liegen sehr viele
Wisp-2-Releases nach dem letzten Wisp-1-Release, finden alte Wisp-1-Installationen neue 1.x-Patches eventuell
nicht mehr. Wisp-1-Patches daher zeitnah veröffentlichen oder Wisp 1 auslaufen lassen.

## Selbst bauen

```bash
dotnet run --project src/Wisp.App                      # LightEdition
dotnet run --project src/Wisp.App -p:WispEdition=AI    # AIEdition
```

Aus `dotnet run` heraus (nicht installiert) ist kein Editionswechsel möglich; die Frage beim ersten Start
erscheint trotzdem und startet dann die gebaute Edition.
