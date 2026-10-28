# Wisp – Offline & Synchronisation

## Grundregeln

1. **Der Server ist die Wahrheit über die Existenz einer Mail.** Wisp löscht eine Mail lokal
   nur dann, wenn der Server *ausdrücklich* bestätigt, dass sie dort nicht mehr existiert.
   Fehler, Timeouts, abgebrochene Verbindungen oder unvollständige Antworten sind **nie**
   ein Beweis für eine Löschung.
2. **Jede Nutzeraktion wird zuerst dauerhaft gespeichert, dann ausgeführt.** Verschieben,
   Löschen, Markieren und Senden landen als Eintrag in der Warteschlange (`pending_ops`),
   bevor irgendetwas anderes passiert. Ein Absturz oder Stromausfall verliert nichts.
3. **Lokales Aufräumen berührt nie den Server.** Wenn Wisp Speicherplatz spart
   (alte Mailtexte aus dem Offline-Cache entfernt), bleibt der Kopf der Mail sichtbar und
   der Inhalt wird bei Bedarf neu geladen. Es wird dabei niemals etwas an den Server gesendet.
4. **Nur IMAP.** POP3 wird bewusst nicht unterstützt, weil es Mails standardmäßig vom
   Server entfernt.

## Woher Wisp weiß, dass eine Mail weg ist

| Signal vom Server                                  | Vertrauen | Folge |
|----------------------------------------------------|-----------|-------|
| `EXPUNGE` / `VANISHED` (QRESYNC) während der Sitzung | ausdrücklich | Mail wird lokal entfernt |
| Mail fehlt in einer **vollständig** abgeschlossenen `UID SEARCH ALL`-Liste | abgeleitet | Mail wird als `missing` markiert und ausgeblendet, aber **nicht gelöscht**. Erst wenn sie auch beim nächsten vollständigen Abgleich fehlt, wird sie entfernt. |
| Abgeleitete Löschung betrifft > 50 % des Ordners (und > 20 Mails) | verdächtig | **Massenlösch-Schutz:** nichts wird ausgeblendet; der Ordner bekommt einen Hinweis. Typische Ursache: Server-Störung. |
| Verbindungsfehler, Timeout, Teilantwort             | keins     | Es passiert gar nichts. Neuer Versuch später. |
| `UIDVALIDITY` hat sich geändert (Server hat Ordner neu nummeriert) | – | Lokale Mails werden über einen Hash der Message-ID den neuen UIDs zugeordnet. Bereits geladene Inhalte bleiben erhalten. Nicht zuordenbare Mails gehen den normalen Zwei-Durchgänge-Weg. |

Taucht eine als `missing` markierte Mail wieder auf, wird die Markierung entfernt.

## Die Warteschlange (`pending_ops`)

Jede Aktion ist ein Eintrag mit `kind`, Ziel (Konto, Ordner, UID), Parametern, Versuchszähler
und Zeitpunkt des nächsten Versuchs.

| Aktion            | Lokal sofort sichtbar             | Auf dem Server | Wenn das Ziel fehlt |
|-------------------|-----------------------------------|----------------|---------------------|
| Gelesen/Ungelesen, Stern | Flag wird sofort gesetzt   | `UID STORE ±FLAGS` (idempotent) | Aktion verfällt still |
| Verschieben/Archivieren | Mail erscheint im Zielordner (als „wird verschoben“) | `UID MOVE` bzw. `COPY` + `\Deleted` + `UID EXPUNGE` | Wisp sucht die Mail per Message-ID im Zielordner; gefunden → erledigt |
| Löschen           | Mail wandert in den Papierkorb    | Verschieben in den Papierkorb | wie Verschieben |
| Endgültig löschen | nur im Papierkorb, nur nach Rückfrage | `\Deleted` + `UID EXPUNGE` | erledigt |
| Senden            | Mail liegt im Postausgang         | SMTP, danach Kopie in „Gesendet“ (falls Anbieter das nicht selbst tut) | – |

Regeln:

- Aktionen eines Kontos werden **in Reihenfolge** abgearbeitet.
- Während eine Aktion aussteht, gewinnt der lokale Zustand über die Flags, die der Server
  meldet – sonst würde ein „gelesen“ beim nächsten Abgleich kurz wieder „ungelesen“.
- Vorübergehende Fehler (Netz, Timeout, Server beschäftigt) → erneuter Versuch mit
  wachsender Pause. Dauerhafte Fehler (Ordner existiert nicht, Rechte fehlen) → Aktion wird
  als fehlgeschlagen markiert, die lokale Ansicht wird zurückgesetzt und der Nutzer sieht
  einen dezenten Hinweis.

## Senden ohne Doppelversand

Der Postausgang (`outbox`) hat Zustände:

```
Queued ──▶ Sending ──▶ Accepted ──▶ (Kopie in "Gesendet") ──▶ entfernt
   ▲          │
   └──────────┘  vorübergehender Fehler: neuer Versuch mit Pause
              │
              └─▶ Failed      dauerhafter Fehler (z. B. Empfänger abgelehnt): Nutzer entscheidet
```

- Die Message-ID wird **beim Erstellen** festgelegt und ändert sich nie.
- Stürzt Wisp ab, während eine Mail im Zustand `Sending` ist, kann niemand sicher wissen, ob
  der Server sie angenommen hat. Wisp sendet dann **nicht automatisch erneut**, sondern
  markiert die Mail als „Unklar“ und prüft, ob sie bereits im Ordner „Gesendet“ liegt.
  Falls nicht, entscheidet der Nutzer mit einem Klick.

## Verbindungsverhalten

- **Zustände:** Online · Eingeschränkt (langsam / Fehler) · Offline. Sichtbar als kleiner
  Punkt in der Seitenleiste.
- **Kurze Timeouts** (Verbindungsaufbau 15 s, einzelne Befehle 60 s), damit ein schlechtes
  Netz die App nicht blockiert.
- **Wiederverbindung mit Backoff:** 5 s, 15 s, 1 min, 5 min, max. 15 min, mit Zufallsanteil.
  Wechselt das Betriebssystem den Netzwerkstatus, wird sofort neu versucht.
- **Kleine Schritte mit Checkpoint:** Kopfzeilen werden in Paketen zu 50 geladen; nach jedem
  Paket wird der Fortschritt gespeichert. Bricht die Verbindung ab, geht es dort weiter.
- **Push statt Polling:** Auf dem Posteingang wird `IDLE` genutzt (alle 9 min erneuert).
  Server ohne IDLE werden alle 5 min abgefragt.
- **Inhalte bei Bedarf:** Zuerst werden nur Kopfzeilen + Vorschautext geladen. Mailtexte der
  letzten 14 Tage (einstellbar) und unter 2 MB werden im Hintergrund für den Offline-Zugriff
  vorgeladen; alles andere beim Öffnen. Ist eine Mail offline nicht verfügbar, sagt Wisp das
  klar, statt einen leeren Text zu zeigen.
