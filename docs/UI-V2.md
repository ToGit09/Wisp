# Wisp 2 – Überarbeitung der Oberfläche

Ziel: Abläufe schneller machen, ohne die Oberfläche voller zu machen. Leitlinien: so wenig sichtbare
Bedienelemente wie möglich, Animationen nur dort, wo sie erklären, was gerade passiert (woher etwas kommt,
wohin es geht), und nie länger als ~0,25 s. Nichts darf flackern, springen oder kurz einen falschen Zustand zeigen.

## Brainstorming

Was kostet beim Mailen am meisten Zeit? Lesen → entscheiden → (antworten | ablegen | löschen) → nächste Mail.
Die Ideen sind danach sortiert, wo sie in diesem Ablauf ansetzen.

**Lesen**
1. Kopfzeile des Lesebereichs sofort aus der Liste füllen (Betreff, Absender, Datum sind schon bekannt),
   statt auf das Laden des Inhalts zu warten. Bisher stand kurz die *vorige* Mail im Kopf.
2. „Wird geladen …“ erst nach 200 ms zeigen – lokale Mails öffnen schneller, das Blinken entfällt.
3. Inhalt beim Mailwechsel weich einblenden (Deckkraft + 6 px Bewegung), alter Inhalt verschwindet sofort.
4. Leertaste springt zur nächsten **ungelesenen** Mail (wie Thunderbird), auch wenn noch keine Mail offen ist.
5. Leerer Lesebereich sagt etwas Nützliches: „3 ungelesen – Leertaste öffnet die nächste“ statt „Keine Mail ausgewählt“.
6. Absender-Avatar im Kopf, Stern/Gelesen-Knöpfe zeigen ihren Zustand.
7. Gruppierung der Liste nach „Heute / Gestern / Diese Woche“.
8. Konversationsansicht (Threads).

**Antworten**
9. **Schnellantwort** direkt unter der Mail: ein Feld, Strg+Enter sendet, mit „Rückgängig“.
   Kurze Antworten („Passt, danke!“) brauchen kein eigenes Fenster. Ein Klick übernimmt den Text ins volle Fenster.
10. Entwurf der Schnellantwort bleibt pro Mail erhalten, wenn man zwischendurch eine andere Mail ansieht.
11. Verfassen-Fenster wie bei Apple Mail: Felder als ruhige Zeilen mit Haarlinien statt Kästen, Senden oben rechts.
12. KI-Antwortvorschläge (AIEdition).

**Ablegen**
13. „Rückgängig“ auch nach Archivieren und Verschieben (bisher nur nach Löschen) – und **Strg+Z** dafür.
14. Toast pausiert, solange die Maus darauf ist (man erreicht „Rückgängig“ immer).
15. Drag & Drop von Mails auf Ordner.
16. „Später erinnern“ (Snooze).

**Navigieren**
17. **Befehlsfeld (Strg+K)**: ein Feld für alles – Ordner, Reiter, Konten, Aktionen, „Verschieben nach …“,
    Suche. Man muss sich keine Tastenkürzel merken; das Feld zeigt sie an und bringt sie so nebenbei bei.
18. Reiterleiste mit gleitender Auswahl (statt springender Hervorhebung).
19. Anzahl ungelesener Mails unter dem Ordnertitel.

**KI (AIEdition)**
20. Zusammenfassung als Vorschauzeile in der Liste – man sortiert aus, ohne zu öffnen.
21. Taste **Z** fasst die offene Mail zusammen; ein pulsierender Funke zeigt, dass gerade geschrieben wird.
22. Modell-Download als schmaler Fortschrittsbalken statt Textmeldung.

## Hinterfragt: Was alle machen

- **Ein Fenster pro Antwort.** Fast alle Clients öffnen für jede Antwort ein volles Editorfenster – auch für
  zwei Wörter. Die meisten Antworten sind kurz. → Schnellantwort (9).
- **„Keine Mail ausgewählt“.** Ein großer leerer Bereich, der nichts sagt. → Zustand + nächster Schritt (5).
- **Werkzeugleisten mit 10+ Symbolen.** Wer sie oft braucht, nimmt Tasten; wer selten, sucht. → Befehlsfeld (17)
  statt zusätzlicher Knöpfe; die vorhandene Leiste bleibt, zeigt aber ihren Zustand.
- **Ladeanzeigen.** Ein „Wird geladen“, das 30 ms sichtbar ist, wirkt wie ein Fehler. → verzögert (2).
- **Vorschauzeile = erste Textzeile.** Die erste Zeile ist oft „Hallo Herr …“ oder „Newsletter ansehen“.
  → in der AIEdition die Zusammenfassung (20).
- **Rückgängig nur beim Löschen.** Jede Aktion, die eine Mail aus der Liste nimmt, kann ein Versehen sein (13).

## Bewertung

| # | Idee | Nutzen | Aufwand | Entscheidung |
|---|------|--------|---------|--------------|
| 1–3 | Sofortiger Kopf, verzögertes Laden, Einblenden | hoch (Anzeigefehler weg) | klein | **umgesetzt** |
| 4–5 | Leertaste → nächste ungelesene, nützlicher Leerzustand | mittel | klein | **umgesetzt** |
| 6 | Avatar + Zustand in der Leiste | mittel | klein | **umgesetzt** |
| 7 | Datumsgruppen | gering | mittel – Kopfzeilen in der Liste stören Tastatur und Mehrfachauswahl | verworfen |
| 8 | Threads | hoch | groß (Speicher, Sync, Liste) | später, eigenes Projekt |
| 9–10 | Schnellantwort mit Entwurf pro Mail | hoch | mittel | **umgesetzt** |
| 11 | Verfassen-Fenster im Apple-Stil | mittel | klein | **umgesetzt** |
| 12 | KI-Antwortvorschläge | mittel | groß; ein 1,7B-Modell formuliert im Namen des Nutzers zu unzuverlässig | verworfen |
| 13–14 | Rückgängig überall, Strg+Z, Toast pausiert | hoch | klein | **umgesetzt** |
| 15 | Drag & Drop | mittel | mittel | später |
| 16 | Snooze | mittel | groß (braucht Serverordner + Zeitplan) | verworfen |
| 17 | Befehlsfeld Strg+K | hoch | mittel | **umgesetzt** |
| 18–19 | Gleitende Reiter, Ungelesen-Zähler | mittel | klein | **umgesetzt** |
| 20–22 | KI: Zusammenfassung in der Liste, Taste Z, Fortschrittsbalken | hoch | klein | **umgesetzt** |

## Animationen

Alle Bewegungen laufen über `Theme/Motion.cs` (Bildwiederholung des Fensters, jederzeit unterbrechbar) oder
über Avalonia-Transitions in den Stilen. Dauer 120–240 ms, Kurve „ease-out“ beim Erscheinen, „ease-in“ beim
Verschwinden. Bewegt wird höchstens 6–12 px; nichts federt nach.

| Wo | Was |
|----|-----|
| Lesebereich | Inhalt blendet ein (180 ms, 6 px), alter Inhalt verschwindet sofort |
| Reiter | Auswahl gleitet zum neuen Reiter (220 ms) |
| Toast | gleitet von unten herein, blendet beim Schließen aus |
| Befehlsfeld | blendet mit 8 px Bewegung ein |
| Knöpfe | drücken sich leicht ein (Skalierung 0,95), Hover-Farben blenden über |
| Liste | Ungelesen-Punkt blendet aus, Hover blendet über |
| Schnellantwort | Feld wächst beim Hineinklicken |
| KI | Funke pulsiert während des Schreibens, Zusammenfassung blendet ein |
