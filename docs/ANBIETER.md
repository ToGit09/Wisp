# Wisp – Unterstützte Anbieter

Wisp erkennt den Anbieter an der E-Mail-Adresse und füllt Server und Ports automatisch aus.
Für unbekannte Domains fragt Wisp die öffentliche Thunderbird-Anbieterdatenbank
(nur die Domain wird übertragen, nie die Adresse). Danach ist manuelle Eingabe möglich.

| Anbieter | Anmeldung | Was der Nutzer vorher tun muss |
|----------|-----------|--------------------------------|
| Gmail / Googlemail | App-Passwort (OAuth2 vorbereitet) | 2-Faktor aktivieren, App-Passwort erzeugen |
| Outlook.com / Hotmail / Live | **OAuth2 (Pflicht)** | nichts – Anmeldung im Browser |
| Yahoo | App-Passwort | App-Passwort in den Kontoeinstellungen erzeugen |
| iCloud | App-spezifisches Passwort | appleid.apple.com → App-spezifische Passwörter |
| GMX | Passwort | In den GMX-Einstellungen „POP3/IMAP Abruf“ aktivieren |
| WEB.DE | Passwort | In den WEB.DE-Einstellungen „POP3/IMAP Abruf“ aktivieren |
| T-Online | E-Mail-Passwort | Im Kundencenter ein separates E-Mail-Passwort setzen |
| Posteo, mailbox.org, AOL, Freenet, 1&1/IONOS | Passwort bzw. App-Passwort | – |
| Proton Mail | über Proton Mail Bridge | Bridge installieren |

## OAuth2 – Hinweis für die Veröffentlichung

Für OAuth2 braucht jede App eine eigene, beim Anbieter registrierte Client-ID. Diese stehen
in `src/Wisp.Core/Accounts/OAuthConfig.cs` und müssen vor einer Veröffentlichung eingetragen
werden:

- **Microsoft:** kostenlose App-Registrierung im Azure-Portal („Öffentlicher Client“,
  Redirect `http://localhost`). Bis dahin funktionieren Outlook.com-Konten nicht, weil
  Microsoft Passwort-Anmeldung per IMAP abgeschaltet hat.
- **Google:** Gmail-Zugriff ist ein „eingeschränkter Bereich“; eine öffentliche App braucht
  eine Sicherheitsprüfung durch Google. Deshalb nutzt Wisp für Gmail standardmäßig
  App-Passwörter, die ohne Prüfung funktionieren.
