# SmtpGateway

*English version: [README.md](README.md)*

[![CI](https://github.com/crossvault/smtp-oauth-gateway/actions/workflows/ci.yml/badge.svg)](https://github.com/crossvault/smtp-oauth-gateway/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Eine alte Anwendung, die nur unverschlüsselte E-Mails im Klartext-SMTP verschicken kann, wieder
zum Versenden bringen - über einen modernen, authentifizierten Maildienst, ganz ohne Änderung am
alten Programm.**

Viele ältere oder lokal betriebene Programme kennen nur einen Weg, Mail zu versenden: einfaches SMTP
ohne Verschlüsselung und ohne Anmeldung. Moderne Maildienste (etwa Microsoft 365) akzeptieren das
nicht mehr. SmtpGateway steht dazwischen. Es läuft als Windows-Dienst auf demselben Rechner, gibt
sich auf `127.0.0.1` (nur der eigene Rechner) als solcher altmodischer Mailserver aus, speichert
jede empfangene Nachricht sicher ab und leitet sie anschließend im Hintergrund über eine
ordentliche, authentifizierte und verschlüsselte Verbindung weiter.

---

## Erste Schritte (die einfache Variante)

Diese Anleitung richtet sich an Personen **ohne** Administrationskenntnisse. Ziel ist einfach: das
alte Programm soll wieder E-Mails verschicken. Die Schritte bitte der Reihe nach ausführen.

Vorab werden zwei Dinge benötigt:

- Ein **Windows-Rechner**, auf dem Programme als Administrator ausgeführt werden können (dort läuft
  bereits die alte Anwendung, oder sie kann den Rechner über `127.0.0.1` erreichen).
- **PowerShell 7 oder neuer** (die Installationsskripte setzen das voraus - die ältere, mit Windows
  gelieferte "Windows PowerShell 5.1" genügt nicht). Falls nicht vorhanden, einmalig aus dem
  Microsoft Store installieren oder in einem Terminal `winget install Microsoft.PowerShell`
  ausführen.

Außerdem wird ein E-Mail-Konto benötigt, über das SmtpGateway *versendet*. Bei Microsoft 365 muss
eine Administratorin oder ein Administrator das Gateway einmalig in der Organisation registrieren -
die genauen Schritte stehen in [docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md)
(empfohlen) oder [docs/microsoft365-setup.md](docs/microsoft365-setup.md). Wer stattdessen ein
normales SMTP-Relay mit Benutzername und Passwort hat, kann die Microsoft-Schritte überspringen.

### 1. Herunterladen

Die **Releases**-Seite des Projekts öffnen:
<https://github.com/crossvault/smtp-oauth-gateway/releases>

Die neueste `.zip`-Datei herunterladen (und, zur Überprüfung, optional die zugehörige
`.sha256`-Prüfsummendatei).

### 2. Entpacken

Das Archiv an einen dauerhaften Ort entpacken, zum Beispiel `C:\SmtpGateway`. Nicht aus dem
Download-Ordner oder vom Desktop betreiben - der Dienst läuft dauerhaft von dem Ort, an den er
entpackt wurde. Im Archiv finden sich unter anderem:

- `service\SmtpGateway.Service.exe` - das Gateway selbst, daneben `appsettings.json`
- `tui\SmtpGateway.Admin.Tui.exe` - ein kleines Verwaltungswerkzeug zur Kontrolle
- `install-service.ps1`, `start-service.ps1`, `stop-service.ps1`, `uninstall-service.ps1`

.NET oder Ähnliches muss **nicht** installiert werden - alles Nötige ist enthalten.

### 3. Die Konfiguration ausfüllen

SmtpGateway wird über eine einzige Datei konfiguriert, `service\appsettings.json`. Es gibt zwei
Wege, sie auszufüllen: den geführten Assistenten des Verwaltungswerkzeugs (am einfachsten) oder das
Bearbeiten der Datei von Hand.

**Variante A (empfohlen): der Einrichtungsassistent.** Das Verwaltungswerkzeug mit dem Befehl
`setup` aufrufen - dafür ist **kein** Administrator-Terminal nötig, da nur eine
Konfigurationsdatei geschrieben wird:

```powershell
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe setup --config C:\SmtpGateway\service\appsettings.json
```

Der Assistent führt durch drei kurze Seiten - eingehendes Empfangen (einfach die Voreinstellung
`127.0.0.1:2525` beibehalten), die Speicherorte und den ausgehenden Provider (Graph / M365Oauth /
GenericSmtp samt dessen Feldern) - und zeigt anschließend eine Übersichtsseite. Ein Klick auf
**Speichern** schreibt `service\appsettings.json` und weist darauf hin, dass ein Neustart des
Dienstes nötig ist; **Abbrechen** schreibt nichts. Existiert die Datei bereits, werden ihre
aktuellen Werte als Vorgaben angeboten und die gesamte Datei beim Speichern neu geschrieben (es wird
keine Sicherungskopie angelegt).

> Das `--config` oben weist den Assistenten auf die `service\appsettings.json` des Dienstes hin. Ohne
> diese Angabe liest und schreibt das Werkzeug eine `appsettings.json` im aktuellen Verzeichnis
> (also neben der ausführbaren Datei im `tui\`-Ordner), und das ist **nicht** die Datei, die der
> Dienst verwendet.

**Variante B: die Datei von Hand bearbeiten.** `service\appsettings.json` in einem Texteditor öffnen
(Notepad genügt). Die im ZIP mitgelieferte Datei enthält alle Optionen mit Kommentaren; auszufüllen
sind nur die Angaben für **eine** Versandart. Hier die kleinste funktionierende Konfiguration für
den häufigsten Fall - Versand über Microsoft 365 mit Microsoft Graph:

```json
{
  "Gateway": {
    "Smtp": {
      "BindEndpoints": [ "127.0.0.1:2525" ]
    },
    "SpoolDirectory": "C:\\ProgramData\\SmtpGateway\\spool",
    "QueueDatabasePath": "C:\\ProgramData\\SmtpGateway\\queue.db",
    "OutboundProvider": {
      "Provider": "Graph",
      "Graph": {
        "TenantId": "00000000-0000-0000-0000-000000000000",
        "ClientId": "00000000-0000-0000-0000-000000000000",
        "ClientSecret": "REPLACE_WITH_YOUR_CLIENT_SECRET",
        "Mailbox": "gateway@ihrefirma.de"
      }
    }
  }
}
```

Die vier `Graph`-Werte durch diejenigen ersetzen, die die Administration beim Befolgen von
[docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md) bereitstellt. `Mailbox` ist die
Adresse, *von* der aus versendet wird.

- Lieber klassisches **Microsoft-365-SMTP** statt Graph? `"Provider": "M365Oauth"` setzen und den
  `M365Oauth`-Block ausfüllen (dieselben vier Werte); siehe
  [docs/microsoft365-setup.md](docs/microsoft365-setup.md).
- Ein einfaches **SMTP-Relay** mit Host, Benutzername und Passwort? `"Provider": "GenericSmtp"`
  setzen und den `GenericSmtp`-Block ausfüllen; siehe [docs/operations.md](docs/operations.md).

Die vollständige Liste aller Einstellungen steht in [docs/configuration.md](docs/configuration.md).
Hinweis: Es gibt **kein automatisches Neuladen** - wird diese Datei später geändert, muss der Dienst
neu gestartet werden (die Skripte aus Schritt 4 erledigen das).

### 4. Den Dienst installieren und starten

Mit der rechten Maustaste auf die Windows-Start-Schaltfläche klicken, **Terminal (Administrator)**
bzw. **PowerShell (Administrator)** wählen und die Rückfrage "Als Administrator ausführen"
bestätigen. Dann diese beiden Befehle ausführen (Pfad anpassen, falls nicht nach `C:\SmtpGateway`
entpackt wurde):

```powershell
C:\SmtpGateway\install-service.ps1 -ExePath C:\SmtpGateway\service\SmtpGateway.Service.exe
C:\SmtpGateway\start-service.ps1
```

Der erste Befehl registriert den Dienst (er startet ab jetzt automatisch mit Windows); der zweite
startet ihn sofort. Zum späteren Stoppen oder Entfernen dienen `stop-service.ps1` und
`uninstall-service.ps1` im selben Administrator-Terminal.

### 5. Die alte Anwendung auf das Gateway zeigen lassen

In den Mail-/SMTP-Einstellungen der Altanwendung Folgendes eintragen:

- **Server / Host:** `127.0.0.1`
- **Port:** `2525` (der Port aus `BindEndpoints` in Schritt 3)
- **Benutzername / Passwort:** keine - leer lassen
- **Verschlüsselung (TLS/SSL/STARTTLS):** keine / aus

Genau das ist der Sinn der Sache: Die Altanwendung spricht im Klartext mit `127.0.0.1`, und
SmtpGateway übernimmt den sicheren, authentifizierten Teil.

### 6. Eine Testnachricht senden und prüfen, ob sie ankam

Eine E-Mail aus der Anwendung versenden. Anschließend im selben Administrator-Terminal das
Verwaltungswerkzeug fragen, was passiert ist:

```powershell
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe status
C:\SmtpGateway\tui\SmtpGateway.Admin.Tui.exe queue list
```

`status` zeigt eine Übersicht; `queue list` zeigt die letzten Nachrichten. Eine angenommene und
zugestellte Nachricht erscheint als `Sent`. Steht sie noch auf `Queued` oder `RetryScheduled`, kurz
warten - das Gateway wiederholt den Versand im Hintergrund. Eine vollständige Schritt-für-Schritt-
Anleitung (auch, wie sich eine Testnachricht ohne die eigene Anwendung senden lässt) steht in
[docs/smoke-test.md](docs/smoke-test.md).

> Das Verwaltungswerkzeug liest dieselbe `appsettings.json`. Wird es aus einem anderen Ordner
> aufgerufen, jeweils `--config C:\SmtpGateway\service\appsettings.json` anhängen. In seiner eigenen
> Hilfe nennt sich das Werkzeug `smtpgw-admin`.

> Tipp: Wird `SmtpGateway.Admin.Tui.exe` **ohne Argumente** aufgerufen, öffnet sich ein interaktives
> Menü (mit Übersicht, Warteschlangen-Browser, Konfigurationsansicht, Ersteinrichtung und
> Provider-Test) - praktisch zum Erkunden, ohne sich Befehlsnamen merken zu müssen. Alle oben
> gezeigten Befehle funktionieren beim Übergeben von Argumenten weiterhin genau wie beschrieben, das
> Skripting bleibt also unberührt.

### Wenn etwas nicht funktioniert

- Der Dienst startet nicht, es wird nichts angenommen, oder Nachrichten bleiben in der Warteschlange
  und werden nie versendet: zunächst [docs/troubleshooting.md](docs/troubleshooting.md) ansehen.
- Nur die ausgehende Verbindung prüfen (funktionieren Anmeldung/Zertifikat?), ohne eine echte
  Nachricht zu senden: `SmtpGateway.Admin.Tui.exe provider test`.

---

## Was es ist und wie es funktioniert (für technische Leser)

SmtpGateway ist ein reines ausgehendes Relay für den Versand. Es ist **kein** Mailserver: kein POP3,
kein IMAP, keine Speicherung eingehender Postfächer. Es nimmt Mail über einen ausschließlich an
Loopback gebundenen SMTP-Listener (Standardeinstellung) an, schreibt jede Nachricht dauerhaft in einen Datei-Spool **und**
eine SQLite-Warteschlange, gibt `250 OK` erst zurück, wenn **beides** festgeschrieben ist, und ein
Hintergrundprozess stellt jede Nachricht anschließend über genau einen konfigurierten Provider mit
Wiederholung und Backoff zu.

```
  Altanwendung
      |  einfaches, nicht authentifiziertes SMTP
      v
  127.0.0.1:2525  ── SmtpGateway (Windows-Dienst) ──────────────────┐
      |                                                              |
      |  annehmen + speichern                                        |
      v                                                              |
  Datei-Spool  +  SQLite-Queue     ->  250 OK erst zurück,           |
      |            (dauerhaft)            wenn BEIDES festgeschrieben |
      v                                                              |
  Zustell-Worker (Wiederholung / Backoff, Status je Empfänger)      |
      |                                                              |
      +---------------------+---------------------+                  |
      v                     v                     v                  |
  Generic SMTP        M365 SMTP OAuth      Microsoft Graph           |
   (TLS-Relay)         (XOAUTH2, 587)      (sendMail, Roh-MIME)      |
      |                     |                     |                  |
      +---------------------+---------------------+------------------+
                            v
                  Postfächer der Empfänger
```

### Funktionen

- **Eingang standardmäßig nur über Loopback.** Der Listener bindet an `127.0.0.1` / `::1` und
  verweigert den Start auf jeder anderen Adresse, sofern dies nicht ausdrücklich mit
  `Smtp:AllowNonLoopbackBind` freigegeben wird. Eine Bindung an eine LAN- bzw. Wildcard-Adresse ist
  möglich, aber bewusst abgesichert: Sie protokolliert unübersehbare Sicherheitswarnungen beim Start
  und sollte mit der optionalen eingehenden SMTP-AUTH kombiniert werden (siehe "An eine
  Netzwerkadresse binden (fortgeschritten)" weiter unten und [docs/security.md](docs/security.md)).
- **Optionale eingehende SMTP-AUTH.** Werden `Smtp:AuthUsername` **und** `Smtp:AuthPassword` gesetzt,
  muss sich jede eingehende Sitzung anmelden (PLAIN/LOGIN); bleiben beide leer (Standard), gibt es
  keine eingehende Authentifizierung. Vor allem für einen netzgebundenen Listener gedacht - beachten:
  Der eingehende Listener hat kein STARTTLS, diese Zugangsdaten laufen also im Klartext über das Netz.
- **Dauerhafte Zustellung, mindestens einmal.** Spool-Datei + SQLite-Queue; `250 OK` erst nach dem
  Festschreiben beider; Wiederholung/Backoff mit Zustellstatus je Empfänger und einer Queue-Gültig-
  keitsdauer (TTL, auf 5 Tage begrenzt).
- **Drei ausgehende Provider, genau einer aktiv:**
  - **Generic SMTP**-Relay mit TLS (STARTTLS oder implizit) und optionaler Anmeldung per
    Benutzername/Passwort.
  - **Microsoft 365 SMTP AUTH OAuth** - XOAUTH2 mit Client-Credentials gegen
    `smtp.office365.com:587`.
  - **Microsoft Graph `sendMail`** - Roh-MIME-Upload, benötigt nur die Anwendungsberechtigung
    `Mail.Send`.
- **Begleitendes Verwaltungswerkzeug** (`SmtpGateway.Admin.Tui.exe`, selbst benannt als
  `smtpgw-admin`) für Status, Ansicht/Wiederholung/Verwerfen/Export der Warteschlange, Anzeigen/
  Bearbeiten/Validieren der Konfiguration und einen aktiven Provider-Test.
- **In sich geschlossen.** Wird als win-x64-ZIP ausgeliefert; Endnutzer installieren kein .NET.
- Optional: Gegendruck (Backpressure) über die Spool-Größe und Ratenbegrenzung des ausgehenden
  Versands.

### An eine Netzwerkadresse binden (fortgeschritten)

> Die empfohlene und voreingestellte Konfiguration belässt den Listener auf Loopback
> (`127.0.0.1`) - die Anfängerschritte oben tun genau das, und die meisten Installationen müssen das
> nie ändern. Nur weiterlesen, wenn eine Altanwendung auf einem **anderen** Rechner das Gateway
> erreichen muss.

Standardmäßig verweigert das Gateway jede Bindung außer an `127.0.0.1` / `::1`, und die Startfehler-
meldung nennt das Flag, das dies aufhebt. Um an eine bestimmte LAN-IP oder eine Wildcard-Adresse
(`0.0.0.0` / `[::]`) zu binden:

1. `Gateway:Smtp:AllowNonLoopbackBind` auf `true` setzen und den Netzwerk-Endpunkt in
   `BindEndpoints` eintragen (z. B. `"192.168.1.10:2525"`). Der Dienst protokolliert dann beim Start
   eine unübersehbare **WARNUNG**, dass er aus dem Netzwerk erreichbar ist.
2. **Eingehende SMTP-AUTH konfigurieren** (empfohlen, nicht erzwungen): sowohl `Smtp:AuthUsername`
   als auch `Smtp:AuthPassword` setzen, damit kein nicht angemeldeter Rechner Mail über den Provider
   weiterleiten kann. Ohne AUTH ist jeder, der den Port erreicht, ein offenes Relay.
3. Beachten: Der eingehende Listener hat **kein STARTTLS** - diese AUTH-Zugangsdaten (und der
   Nachrichteninhalt) laufen im **Klartext** über das Netz. Auf diesem Netzsegment als abhörbar
   behandeln.
4. Den Port mit der **Windows-Firewall** auf die konkreten Quell-Rechner beschränken.

Wie bei jeder Konfigurationsänderung ist ein Neustart des Dienstes erforderlich. Die vollständige
Warnmatrix steht in [docs/security.md](docs/security.md), die genauen Schlüssel und Validierungs-
regeln in [docs/configuration.md](docs/configuration.md).

### Dokumentation

| Dokument | Inhalt |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Projektaufbau, Eingangsfluss, Zustandsautomat der Warteschlange, ausgehende Provider, Zustell-Worker, Backpressure/Ratenbegrenzung |
| [docs/configuration.md](docs/configuration.md) | Vollständige `appsettings.json`-Referenz, Standardwerte, Validierungsregeln, Überschreiben per Umgebungsvariablen |
| [docs/operations.md](docs/operations.md) | Betrieb als Windows-Dienst, Protokolle, Befehlsreferenz des Verwaltungswerkzeugs, Einrichtung eines generischen SMTP-Relays |
| [docs/microsoft365-setup.md](docs/microsoft365-setup.md) | Einrichtung von Microsoft 365 SMTP AUTH OAuth (Entra-App, Berechtigungen, PowerShell-Schritte) |
| [docs/microsoft-graph-setup.md](docs/microsoft-graph-setup.md) | Einrichtung des Microsoft-Graph-`sendMail`-Providers |
| [docs/security.md](docs/security.md) | Sicherheitsmodell: Loopback-only, TLS, OAuth, Umgang mit Geheimnissen, Entscheidungen zur Spool-Speicherung |
| [docs/queue.md](docs/queue.md) | Aufbau von Warteschlange/Spool, Wiederholungs-/Backoff-Zeitplan, TTL, Status je Empfänger, Einschränkungen |
| [docs/testing.md](docs/testing.md) | Testprojekte, Ausführung, Ansatz zur Testabdeckung |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Häufige Fehlerbilder und was zu prüfen ist |
| [docs/smoke-test.md](docs/smoke-test.md) | End-to-End-Rauchtest mit einem echten SMTP-Client |

### Das Verwaltungswerkzeug im Überblick

```
smtpgw-admin                        # ohne Argumente: interaktives Menü öffnen (Übersicht, Warteschlange, Konfiguration, Einrichtung, Test)
smtpgw-admin setup                  # Ersteinrichtungs-Assistent: appsettings.json ausfüllen (Eingang, Speicher, Provider)
smtpgw-admin status                 # Übersicht zu Warteschlange und Provider
smtpgw-admin queue list             # Einträge auflisten (Filter mit --status)
smtpgw-admin queue show <id>        # vollständige Details zu einem Eintrag
smtpgw-admin queue retry <id>       # nicht versendete Empfänger auf "retryable" zurücksetzen
smtpgw-admin queue discard <id>     # weitere Zustellversuche stoppen
smtpgw-admin queue export <id>      # Roh-MIME nach exports/<id>.eml schreiben
smtpgw-admin config show            # alle Gateway-Einstellungen anzeigen (Geheimnisse im Klartext)
smtpgw-admin config set <pfad> <w>  # eine Einstellung setzen, z. B. Smtp:MaxRecipients
smtpgw-admin config validate        # appsettings.json validieren
smtpgw-admin provider test          # Verbindungs-/Funktionsprüfung des aktiven Providers
```

Die ausführbare Datei im ZIP ist `tui\SmtpGateway.Admin.Tui.exe`; `smtpgw-admin` ist der Name, den
sie in ihrer eigenen Hilfe verwendet. Beim Aufruf aus einem anderen Verzeichnis jeweils
`--config <pfad-zur-appsettings.json>` anhängen.

---

## Aus dem Quellcode bauen

Benötigt wird das **.NET-10-SDK** (per `global.json` auf `10.0.301` festgelegt) unter Windows. Im
Wurzelverzeichnis des Repositorys:

```powershell
git clone https://github.com/crossvault/smtp-oauth-gateway.git
cd smtp-oauth-gateway
dotnet build
dotnet test
```

- Beim Bauen werden **alle Compiler-Warnungen als Fehler** behandelt (`Directory.Build.props`).
- Die Tests verwenden **xUnit v3** auf dem **Microsoft.Testing.Platform**-Runner; `dotnet test`
  bleibt der Einstiegspunkt und benötigt keine besonderen Schalter.
- Die Live-End-to-End-Suite (`tests/SmtpGateway.E2ETests`) versendet echte Mail über einen
  Microsoft-365-Tenant und ist **optional** - sie überspringt sich sauber selbst, wenn keine
  Zugangsdaten konfiguriert sind, sodass ein normaler `dotnet test`-Lauf nie das Netzwerk berührt.
- Ein Release-ZIP selbst erzeugen: `./build-release.ps1 -Version 0.1.0` (in sich geschlossene
  Single-File-win-x64-Builds von Dienst und Verwaltungswerkzeug, dazu die Skripte, eine
  Beispielkonfiguration, `LICENSE`, eine SBOM und Drittanbieter-Hinweise).

Die CI (`.github/workflows/ci.yml`) stellt wieder her, baut im Release-Modus, führt die Tests aus und
prüft die Abhängigkeiten auf bekannte Schwachstellen, jeweils auf `windows-latest`.

## Bekannte Einschränkungen

Die Zustellung erfolgt **mindestens einmal** ("at-least-once"), sodass eine Nachricht in seltenen
Absturz-/Wiederholungsfällen mehr als einmal zugestellt werden kann - SmtpGateway daher nicht dort
einsetzen, wo eine genau-einmalige Zustellung zwingend ist. Der Eingang ist **standardmäßig nur über
Loopback** erreichbar; eine Bindung an eine Netzwerkadresse ist ausschließlich über die ausdrückliche
Freigabe `Smtp:AllowNonLoopbackBind` möglich und wird dringend abgeraten, sofern nicht zusätzlich die
eingehende SMTP-AUTH konfiguriert und der Port per Firewall abgesichert wird (siehe
[docs/security.md](docs/security.md)). Der Dienst läuft **ausschließlich unter Windows** (er setzt auf
das Windows-Dienst-Hosting und wird als win-x64 veröffentlicht). Die genauen Zusicherungen stehen in
[docs/queue.md](docs/queue.md) und [docs/security.md](docs/security.md).

## Mitwirken

Beiträge sind willkommen - siehe [CONTRIBUTING.md](CONTRIBUTING.md).

## Lizenz und Gewährleistung

SmtpGateway wird unter der **MIT-Lizenz** veröffentlicht, Copyright (c) crossVault GmbH. Der
vollständige Text steht in [LICENSE](LICENSE).

**Diese Software wird "wie besehen" ("as is") bereitgestellt, ohne jegliche Gewährleistung, weder
ausdrücklich noch stillschweigend.** Die Nutzung erfolgt auf eigenes Risiko; die crossVault GmbH
übernimmt keine Haftung für Schäden, Datenverluste oder nicht bzw. doppelt zugestellte E-Mails, die
aus der Nutzung entstehen.
