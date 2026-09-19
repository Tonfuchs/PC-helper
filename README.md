# PC Helper

**Diagnose- und Überwachungswerkzeug für Windows-Probleme aller Art** – vom Mikrofon, das Discord
nicht findet, über abbrechende Internetverbindungen bis zum Bildschirm, der einfach schwarz wird.

Der Einstieg ist bewusst nicht technisch: Man beschreibt in eigenen Worten, **was nicht
funktioniert**. PC Helper sucht den passenden bekannten Fall heraus, sagt, was typischerweise
dahintersteckt, und untersucht dann gezielt in diese Richtung – statt stumpf alles zu prüfen.

Drei Bausteine:

1. **Problem melden** – Symptom eingeben oder auswählen. Daraus ergeben sich die passenden
   Prüfungen, Sofortschritte, Reparaturen und Windows-Werkzeuge.
2. **Diagnose** – Prüfung von Hardware, Treibern, Ton, Netzwerk, Geräten, Energieeinstellungen und
   Ereignisprotokoll, verdichtet zu einer nachvollziehbaren Verdachtsreihenfolge.
3. **Dauerüberwachung** – zeichnet im Hintergrund fortlaufend Messwerte auf, damit nach einem
   Vorfall rekonstruierbar ist, was unmittelbar davor passiert ist. Auch dann, wenn der Rechner
   hart ausgegangen ist.

Windows, .NET 8, WPF. Quelloffen unter der MIT-Lizenz.

---

## Für Anwender

### Installation

1. Unter [Releases](../../releases) die Datei **`PCHelper.exe`** herunterladen.
2. Doppelklicken. Fertig – es gibt keine Installation, keine Abhängigkeiten, keine Registry-Einträge
   außer dem optionalen Autostart.

> **SmartScreen-Hinweis:** Die Datei ist nicht signiert (Code-Signing-Zertifikate kosten Geld).
> Windows zeigt daher beim ersten Start „Der Computer wurde durch Windows geschützt“.
> Über *Weitere Informationen → Trotzdem ausführen* startet die App.
> Wer das prüfen möchte: Im Release liegt `SHA256SUMS.txt` mit der Prüfsumme, und die App
> verifiziert bei ihren eigenen Updates auch automatisch dagegen.

### Ein Problem melden

Auf der Seite **Problem melden** beschreibt man in einem Satz, was los ist – zum Beispiel
*„mein Mikrofon wird in Discord nicht erkannt“*. Die App vergleicht das mit ihrem Katalog
bekannter Fälle und zeigt die besten Treffer. Zum gewählten Fall erscheinen dann:

- **Was typischerweise dahintersteckt** – die wahrscheinlichen Ursachenbereiche im Klartext.
- **Was sich sofort selbst prüfen lässt** – zwei Minuten Handarbeit, die oft schon reichen.
- **Passende Werkzeuge** – öffnen die zuständige Windows-Stelle direkt (etwa
  *Datenschutz: Mikrofon* oder die klassische Sound-Systemsteuerung, in der deaktivierte
  Geräte überhaupt erst sichtbar werden).
- **Passende Reparaturen** – umkehrbare Ein-Klick-Änderungen.
- **Gezielte Untersuchung starten** – es laufen nur die Prüfungen, die zu diesem Problem etwas
  beitragen können. Die Befunde stehen danach nach ihrer Bedeutung für genau dieses Problem,
  nicht nach Schweregrad.

Der Katalog deckt sechs Bereiche ab: Bild und Anzeige, Ton und Mikrofon, Internet und Netzwerk,
Leistung und Abstürze, Geräte und Anschlüsse, Windows und Datenträger. Passt nichts davon, führt
**„Alles prüfen“** weiterhin den vollständigen Rundumlauf aus.

#### Beispiel: „Discord findet mein Mikrofon nicht“

Der häufigste Grund ist keine Hardware, sondern eine Berechtigung: Windows führt **zwei getrennte
Schalter** für den Mikrofonzugriff – einen für Store-Apps und einen für klassische Desktop-Programme
(*„Desktop-Apps den Zugriff auf Ihr Mikrofon erlauben“*). Der zweite steht unterhalb einer langen
App-Liste und wird fast immer übersehen. Betroffene Programme melden dann nicht *„Zugriff
verweigert“*, sondern *„kein Mikrofon gefunden“* – weshalb an dieser Stelle typischerweise
stundenlang an Treibern gesucht wird.

PC Helper liest beide Schalter, die per Gruppenrichtlinie gesetzten Sperren und die einzeln
gesperrten Anwendungen direkt aus – und bietet die Freigabe als Reparatur an. Ebenso wird die
Geräteliste **inklusive der deaktivierten und abgesteckten Geräte** aus der Registrierung gelesen,
denn genau die blendet Windows in den Sound-Einstellungen standardmäßig aus.

### Vorgehen bei sporadischen Schwarzbildern

1. **„Jetzt prüfen“** auf der Übersichtsseite. Dauert wenige Sekunden.
2. Die **Dauerüberwachung** eingeschaltet lassen und *Mit Windows starten* aktivieren.
   Nur was aufgezeichnet wird, kann später ausgewertet werden.
3. Tritt das Problem auf: sobald das Bild zurück ist, auf **„Vorfall jetzt melden“** klicken und
   kurz beschreiben, was passiert ist. Der Zeitpunkt wird mit den Messwerten davor verknüpft.
   *Das geht auch über das Symbol im Infobereich der Taskleiste – ohne das Fenster zu öffnen.*
4. Hilft nur ein **Neustart**? Dann fragt die App nach dem Hochfahren einmal nach:
   *„Der Rechner wurde am … neu gestartet. War das nötig, weil der Bildschirm schwarz war?“*
   Ein Klick auf **„Ja, Schwarzbild“** genügt – der Zeitpunkt ist damit belegt.
5. **Immer nur eine Sache auf einmal ändern** und danach zwei bis drei Tage beobachten.
   Wer drei Dinge gleichzeitig ändert, weiß am Ende nicht, welche geholfen hat.
6. **Bericht exportieren** – zwei Formate:
   - **PDF** für Support-Anfragen und Garantiefälle. Öffnet überall, lässt sich an jedes Ticket
     anhängen, kompakt aufgebaut (Zusammenfassung → Hardware → Neustart-Verlauf → Befunde).
   - **HTML** zum Selberlesen im Browser – ausführlicher, mit allen Rohdaten zum Aufklappen.

### Bluescreens: Stoppcodes im Klartext

Findet die App Bluescreens, liest sie den **Stoppcode** aus Ereignis 1001 bzw. aus dem Feld
`BugcheckCode` von Kernel-Power 41, übersetzt ihn (`0x0000001A → MEMORY_MANAGEMENT`) und leitet
daraus die wahrscheinliche Ursache ab. Die Abstürze werden nach Stoppcode gruppiert – treten drei
oder mehr *verschiedene* Codes auf, weist die App ausdrücklich darauf hin, dass wahllos wechselnde
Stoppcodes für instabile Hardware sprechen und nicht für einen einzelnen defekten Treiber.

Ebenso wird Ereignis 41 aufgeschlüsselt, statt nur gezählt:

| Erkennung | Bedeutung |
| --- | --- |
| `BugcheckCode` ≠ 0 | Es ging ein Bluescreen voraus – kein eigenständiger Vorfall |
| `PowerButtonTimestamp` ≠ 0 | Jemand hat den **Netzschalter gedrückt** – typisch nach einem Schwarzbild |
| beides 0 | Der Rechner ist ohne Vorwarnung ausgegangen oder eingefroren |

Diese Unterscheidung ist wichtig: Eine hohe Gesamtzahl an „unerwarteten Abschaltungen“ wirkt
dramatisch, besteht aber oft überwiegend aus selbst ausgelösten Neustarts. Nur die dritte Kategorie
deutet wirklich auf die Stromversorgung hin.

### Neustart-Protokoll

Wenn ein Schwarzbild nur durch einen Neustart wegzubekommen ist, dann ist **jeder Neustart selbst
ein Fehlermarker**. Darum protokolliert PC Helper das Startverhalten aus drei Quellen:

- **Beim Herunterfahren und Neustarten** schreibt die App automatisch einen Eintrag mit Zeitpunkt,
  Sitzungsdauer, letzten Messwerten und der Anzeigekonfiguration – in
  `%LOCALAPPDATA%\PCHelper\sitzungsprotokoll.txt`. Diese Datei lässt sich direkt weitergeben.
- **Beim Hochfahren** kommt ein Eintrag dazu, inklusive der Feststellung, ob die vorherige Sitzung
  sauber endete. Endete sie nicht sauber, ist das letzte Lebenszeichen der exakte Ausfallzeitpunkt.
- **Rückwirkend** rekonstruiert die App aus dem Windows-Ereignisprotokoll alle Startvorgänge der
  letzten 30 Tage (Kernel-General 12/13, ersatzweise EventLog 6005/6006) – auch aus der Zeit vor
  der Installation. Sitzungen ohne ordentliches Herunterfahren werden rot markiert.

Alles zusammen landet im Bericht und fließt in die Prüfung *Neustart-Verlauf* ein.

### Was die App automatisch erkennt

- **Verlust der Bildschirmverbindung.** Ändert sich die aktive Anzeigekonfiguration, ohne dass
  jemand etwas umgesteckt hat, ist genau das der gesuchte Signalabriss. Wird als Vorfall festgehalten.
- **Harte Ausfälle.** Beim Beenden schreibt die App ein Lebenszeichen. Fehlt es beim nächsten Start,
  ist der Zeitpunkt des letzten Lebenszeichens der Moment, in dem der Rechner ausgefallen ist.
- **Neustarts und Herunterfahren.** Automatisch protokolliert – siehe unten.
- **Grafiktreiber-Resets (TDR), WHEA-Hardwarefehler, Bluescreens, Live-Kernel-Berichte** aus dem
  Ereignisprotokoll der letzten 30 Tage.
- **EXPO/XMP**, BIOS-Alter, Energieeinstellungen, Overlay- und Tuning-Software, Datenträgerzustand.
- **Audiogeräte** inklusive der deaktivierten und abgesteckten – die Windows in den
  Sound-Einstellungen ausblendet – sowie den Zustand der beiden Audiodienste.
- **Mikrofon- und Kamerazugriff**: globale Einstellung, der Schalter für Desktop-Programme,
  Gruppenrichtlinien und einzeln gesperrte Anwendungen.
- **Netzwerk**: Adapterzustand, IP-Konfiguration, DHCP-Ausfall (169.254er-Adresse), zu langsam
  ausgehandelte Kabelverbindungen – dazu ein Erreichbarkeitstest, der **Router, Internet und
  Namensauflösung getrennt** prüft. Erst diese Dreiteilung sagt, wo die Kette reißt.
- **Der Weg ins Internet**: Läuft ein VPN, und wenn ja, was kostet der Umweg? Router, VPN-Einstieg und
  Internet werden getrennt gemessen – daran sieht man, ob die Bremse im Haus sitzt, beim VPN oder
  beim Anbieter. Dazu die **Ladezeit eines einzelnen kleinen Abrufs** (das Gefühl „die Seite lädt
  ewig“, obwohl die Leitung schnell ist) und das **Tempo der Namensauflösung** einschließlich Vergleich
  mit anderen Namensservern.
- **Netzwerkeinstellungen, die bremsen**: eingetragener Proxy, automatische Proxy-Suche, Umleitungen in
  der `hosts`-Datei, verstelltes TCP-Empfangsfenster, Kabel und WLAN gleichzeitig, Netzwerkkarten-
  Karteileichen.
- **Geräte mit Fehlercode** im Geräte-Manager, mit Übersetzung der Codes (10, 22, 28, 43 …).
- **USB-Ereignisse**, das selektive USB-Energiesparen und das **Stromsparen einzelner Geräte** (das
  Häkchen im Geräte-Manager) als Ursache von Aussetzern, dazu hängengebliebene „Unbekanntes
  USB-Gerät“-Einträge, mehrere Kameras und Programme, die die Kamera gerade belegen.
- **Prozessorlast** mit Benennung der Verursacher, Speicherbelegung und Autostart-Umfang.
- **Systemhygiene**: Energiesparmodus als Handbremse, wie lange der letzte *echte* Start her ist
  (bei aktivem Schnellstart ist „Herunterfahren“ keiner), mehrere gleichzeitig aktive Virenscanner.

### Reparaturen

Der Bereich *Reparaturen* enthält nachvollziehbare Systemänderungen – Schnellstart abschalten,
Bildschirm-Timeout aus, PCIe-Energieverwaltung aus, hardwarebeschleunigte GPU-Planung aus, und
weitere. Dabei gilt:

- **Nichts passiert von allein.** Jede Änderung erfordert eine ausdrückliche Bestätigung.
- **Der genaue Befehl steht immer dabei**, bevor er ausgeführt wird.
- **Fast alles ist umkehrbar** – mit einem Klick auf *Zurücknehmen*.
- Auf Wunsch wird vor der ersten Änderung ein **Systemwiederherstellungspunkt** angelegt.

### Wartung

Der Bereich *Wartung* ist für alles, was nicht nur gemeldet, sondern auf Knopfdruck erledigt werden
soll. **Prüfen ist dort immer folgenlos** – geändert wird nur, was ausdrücklich angeklickt wird, und
nach jeder Aktion liest die App den Zustand neu ein und meldet, was wirklich Sache ist (nicht, was
das Werkzeug zurückgemeldet hat).

- **Autostart** zeigt *alle* Startwege an einer Stelle: Registry, Autostart-Ordner, Aufgabenplanung
  und fremde Dienste auf „Automatisch“ – auch die, die der Task-Manager verschweigt. Zu jedem Eintrag
  steht in normalem Deutsch, was das Programm ist und ob es mitstarten muss; bei kryptischen Namen
  liest die App Hersteller und Beschreibung aus der Programmdatei. Rauswerfen ist umkehrbar, das
  Programm bleibt installiert. Bei Hardware- und Schutzsoftware (Lüftersteuerung, Treiber, Virenschutz,
  Anti-Cheat) kommt vorher einmal eine Rückfrage. Was die App nicht kennt, lässt sich per Knopf im
  Browser nachschlagen – die Suche wird nur auf Klick geöffnet.
- **Platz schaffen** rechnet zusammen, was wo liegt, *bevor* etwas gelöscht wird: Update-Reste,
  Zwischenablage-Ordner, Papierkorb, Absturzberichte. Gelöscht wird konservativ – Zwischenablage-Ordner
  nur, was älter als sieben Tage ist, Verknüpfungen werden weder betreten noch angefasst, was in Benutzung
  ist bleibt liegen. `Windows.old` löscht die App nie selbst, sie öffnet die Datenträgerbereinigung.
  Downloads, Desktop, Browser- und Steam-Zwischenspeicher werden nur *gezeigt*. Die Windows-eigenen
  Ordner sind nur sichtbar, wenn PC Helper als Administrator läuft; die Ansicht sagt das ausdrücklich.
- **Geräte** ist der Ersatz für die abgeschafften Windows-Problembehandlungen. Fehlercodes werden in
  normales Deutsch übersetzt, und **„Neu einstecken“** startet ein Gerät per Software neu – dasselbe
  wie Kabel raus und wieder rein. Dazu: hängengebliebene USB-Anmeldungen wegräumen, USB-Stromsparen im
  Energieplan und pro Gerät abschalten, Kameras einzeln.

**„Ist Absicht“.** Nicht jede Auffälligkeit ist ein Fehler. Jeder gelbe oder rote Punkt in der Wartung
hat einen Knopf *Ist Absicht*: Der Punkt gilt dann als in Ordnung, steht ganz unten und zählt nicht
mehr als Problem. Der Knopf wird zu *Doch wieder melden* – rückgängig geht jederzeit. Gespeichert wird
das in den Einstellungen, nur auf diesem Rechner.

Wartungsaktionen, die Administratorrechte brauchen, laufen als kurzes PowerShell-Skript mit einer
UAC-Abfrage je Aktion. Die Skripte liegen danach unter `%LOCALAPPDATA%\PCHelper\fixes\` zum Nachlesen.

### Datenschutz

Alle Daten bleiben auf dem Rechner (`%LOCALAPPDATA%\PCHelper`, Berichte unter
`Dokumente\PC Helper\Berichte`). Nach außen gehen ausschließlich zwei Abrufe an GitHub:
die Update-Prüfung und das Nachladen der Wissensdatenbank. Es wird nichts hochgeladen.

Eine Ausnahme ist der **Erreichbarkeitstest** der Netzwerkprüfung: Er sendet je vier Ping-Pakete an
das eigene Standardgateway und an `1.1.1.1` und löst einmal `www.msftconnecttest.com` auf – anders
lässt sich nicht feststellen, ob die Verbindung am Router, am Anschluss oder an der Namensauflösung
scheitert. Es werden dabei keinerlei Inhalte übertragen. Der Test läuft nur mit, wenn ein
Netzwerk-Symptom gewählt wurde oder die vollständige Prüfung ausgeführt wird.

Dieselbe Ausnahme gilt für die beiden Messungen zum Tempo:

- Die **Ladezeit-Messung** ruft je viermal eine winzige Datei (wenige Kilobyte) von
  `www.wikipedia.org`, `www.cloudflare.com` und `www.google.com` ab, jedes Mal mit frischer
  Verbindung – gemessen wird nur die Zeit, es werden keine Inhalte ausgewertet.
- Die **DNS-Messung** stellt je vier Namensanfragen an die eingetragenen Namensserver und eine
  Vergleichsanfrage (`www.wikipedia.org`) an den Router sowie an `1.1.1.1`, `8.8.8.8` und `9.9.9.9`.

Alle übrigen Wartungsfunktionen arbeiten rein lokal. Nur die Suche „Im Web nachsehen“ öffnet den
Browser – und nur auf Klick.

Ein erzeugter Bericht enthält allerdings Hardware- und Ereignisprotokolldaten inklusive Geräte- und
teilweise Benutzernamen. Vor dem Weitergeben also kurz durchsehen.

---

## Für Betreiber des Repositorys

### Repository anpassen

In [`src/PCHelper/Core/AppInfo.cs`](src/PCHelper/Core/AppInfo.cs) stehen Besitzer und Name des
Repositorys:

```csharp
public const string DefaultRepoOwner = "Tonfuchs";
public const string DefaultRepoName  = "PC-helper";
```

Beides lässt sich zusätzlich zur Laufzeit unter *Einstellungen* überschreiben – praktisch zum Testen
eines Forks, ohne neu zu bauen.

### Eine neue Version veröffentlichen

```bash
git tag v1.1.0
git push origin v1.1.0
```

Mehr nicht. Der Workflow [`release.yml`](.github/workflows/release.yml) übernimmt den Rest:

1. baut eine eigenständige Single-File-EXE (kein .NET-Runtime nötig, ca. 63 MB),
2. prüft sie mit einem Rauchtest,
3. erzeugt `SHA256SUMS.txt`,
4. legt das GitHub-Release an und hängt beide Dateien an.

Die Versionsnummer kommt aus dem Tag – im Code muss nichts angepasst werden.
Alternativ lässt sich der Workflow von Hand mit einer Versionsangabe starten (*workflow_dispatch*).

### Wie das Update beim Anwender ankommt

Die App fragt beim Start das neueste Release über die GitHub-API ab und vergleicht die Version.
Ist eine neuere vorhanden, erscheint oben ein Banner. Nach dem Klick auf *Jetzt aktualisieren*:

1. `PCHelper.exe` wird heruntergeladen,
2. die SHA256-Prüfsumme gegen `SHA256SUMS.txt` verifiziert (bei Abweichung: Abbruch),
3. ein kleines Hilfsskript wartet, bis sich das Programm beendet hat, tauscht die EXE aus
   und startet sie neu.

Es ist also nur ein `git push` des Tags nötig – der Anwender bekommt das Update angeboten.

### Wissensdatenbank ohne App-Update erweitern

[`knowledge/known-issues.json`](knowledge/known-issues.json) enthält bekannte Problemmuster mit
Bedingungen, unter denen sie zutreffen. Die Datei ist in die EXE eingebettet **und** wird beim Start
aus dem `main`-Branch nachgeladen. Ein Eintrag dort wirkt damit sofort bei allen Anwendern – ohne
neues Release.

```jsonc
{
  "id": "kb-beispiel",
  "title": "Kurzer, klarer Titel",
  "category": "Grafik",
  "severity": "warning",              // info | warning | critical
  "match": {
    "gpuNameContains": ["RTX 50"],    // alle angegebenen Bedingungen müssen zutreffen
    "requiresDisplayPort": true,
    "memoryOverclocked": true,
    "fastStartupEnabled": true,
    "biosOlderThanDays": 240,
    "cpuNameContains": ["Ryzen 9"],
    "boardContains": ["X870"],
    "processNameContains": ["iCUE"],
    "hasInactiveMicrophone": true,    // Aufnahmegerät deaktiviert oder abgesteckt
    "microphoneBlocked": true,        // Windows sperrt den Mikrofonzugriff
    "hasProblemDevice": true,         // Gerät mit Fehlercode im Geräte-Manager
    "hasWifiAdapter": true
  },
  "summary": "Ein Satz für die Übersicht.",
  "detail": "Ausführliche Erklärung.",
  "recommendation": "Was konkret zu tun ist.",
  "causes": { "GpuDriver": 0.4, "DisplayLink": 0.6 },
  "symptomIds": ["mic-not-in-app"],   // stellt den Fall bei diesem Problem nach ganz oben
  "fixIds": ["mic-privacy-allow"],
  "links": [{ "label": "Quelle", "url": "https://..." }]
}
```

Gültige Ursachenbereiche für `causes`: `GpuDriver`, `DisplayLink`, `Memory`, `PowerSupply`,
`PowerSettings`, `Thermal`, `Storage`, `Software`, `Bios`, `OperatingSystem`, `AudioDevice`,
`Microphone`, `AppPermission`, `Network`, `Wifi`, `UsbDevice`, `DeviceDriver`, `Cpu`.
Der CI-Workflow prüft bei jedem Push, ob die Datei gültiges JSON ist und die IDs eindeutig sind.

Über `symptomIds` lässt sich ein neuer „das kenne ich“-Fall **ohne App-Update** an ein bereits
vorhandenes Symptom hängen. Gültige Symptom-IDs stehen in
[`Diagnostics/Symptoms.cs`](src/PCHelper/Diagnostics/Symptoms.cs).

### Ein neues Symptom ergänzen

Eintrag in `SymptomCatalog.All` in [`Diagnostics/Symptoms.cs`](src/PCHelper/Diagnostics/Symptoms.cs)
anlegen. Ein Symptom besteht aus:

| Feld | Bedeutung |
| --- | --- |
| `Title` | Wie ein Anwender es formulieren würde – nicht wie ein Techniker |
| `Description` | Was typischerweise dahintersteckt, zwei bis drei Sätze |
| `Keywords` | Begriffe für die Freitextsuche, Umgangssprache ausdrücklich erwünscht |
| `Causes` | Vorabgewichtung der Ursachenbereiche (0…1) |
| `FirstSteps` | Was sich in zwei Minuten ohne Werkzeug prüfen lässt |
| `FixIds` / `ToolIds` | Verweise auf `FixCatalog` bzw. die Werkzeugliste |

`Causes` ist der Dreh- und Angelpunkt: Daraus ergibt sich automatisch, **welche Prüfungen laufen**
(alle mit passendem `Topics`-Eintrag), wie die Befunde sortiert werden und wie stark die
Verdachtsliste in diese Richtung gewichtet wird. Es ist also keine weitere Verdrahtung nötig.

`PCHelper.exe --selftest` prüft, ob alle `FixIds` und `ToolIds` eines Symptoms auch existieren.

### Eine neue Prüfung ergänzen

1. Klasse anlegen, die `ICheck` implementiert (Vorlagen in
   [`src/PCHelper/Diagnostics/Checks/`](src/PCHelper/Diagnostics/Checks/)).
2. `Topics` setzen – die Ursachenbereiche, zu denen die Prüfung etwas beitragen kann. Eine leere
   Liste bedeutet „gehört zur Grundlage“ und läuft immer mit (Systemübersicht, Wissensdatenbank,
   Fehlerprotokoll).
3. In `CheckEngine.All()` registrieren – die Reihenfolge bestimmt die Anzeige.

Ein `Finding` trägt neben Titel und Text eine Gewichtung auf Ursachenbereiche (`Causes`).
Daraus errechnet die `SuspicionEngine` die Verdachtsreihenfolge; der Schweregrad bestimmt,
wie stark ein Befund zählt. Befunde mit `Severity.Ok` erhöhen keinen Verdacht.

Zusätzlich kann ein `Finding` über `SymptomIds` angeben, auf welche Symptome es **unmittelbar
antwortet**. Solche Befunde stehen bei der gezielten Untersuchung ganz oben – unabhängig davon,
ob sie als „kritisch“ oder nur als „auffällig“ eingestuft sind.

### Eine neue Reparatur ergänzen

Eintrag in `FixCatalog.All` anlegen: Titel, Beschreibung, Begründung, die auszuführenden Befehle und
möglichst die Befehle zur Rücknahme. Alles Weitere – UAC-Abfrage, Protokollierung, Anzeige des
Befehls vor der Ausführung – erledigt die Oberfläche automatisch.

Ändert eine Reparatur den **Benutzerzweig der Registrierung** (`HKCU`), muss `RequiresAdmin = false`
gesetzt werden. Sonst würde bei einer Elevation mit einem anderen Konto dessen Benutzerzweig
geändert – und beim angemeldeten Benutzer bliebe alles beim Alten.

### Selbst bauen

```bash
dotnet build PCHelper.sln -c Release

# Eigenständige EXE wie im Release
dotnet publish src/PCHelper/PCHelper.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Voraussetzung: .NET 8 SDK.

### Kopfloser Modus

```bash
PCHelper.exe --selftest
```

Führt ohne Oberfläche aus:

1. eine vollständige Diagnose, schreibt HTML- und PDF-Bericht,
2. eine Prüfung des Symptomkatalogs gegen Reparatur- und Werkzeugliste (findet Tippfehler in
   Verweisen),
3. einen Aufbau des Hauptfensters samt aller Seiten ohne Anzeige – fehlende XAML-Ressourcen fallen
   sonst erst auf, wenn jemand die betreffende Seite öffnet,
4. eine Prüfung aller drei Wartungsbereiche (ohne je eine Aktion auszuführen – der Selbsttest darf
   nichts am System ändern) und eine Syntaxprüfung der PowerShell-Skripte, die die Wartung mit
   Administratorrechten startet – so lassen sie sich ohne UAC-Abfrage testen,
5. eine gezielte Untersuchung inklusive Freitext-Zuordnung als Beispiel.

Ist die Umgebungsvariable `PCHELPER_SELFTEST_SHOTS` auf einen Ordner gesetzt, legt der Selbsttest
zusätzlich von jedem Wartungsbereich ein Bild ab – praktisch, um Änderungen an der Oberfläche ohne
Klicken zu prüfen.

Alles wird nach `%LOCALAPPDATA%\PCHelper\pchelper.log` protokolliert; Exitcode 0 (bzw. 1 bei
Fehler). Wird auch im CI-Workflow als Rauchtest verwendet. Der Selbsttest ist von der
Einzelinstanz-Sperre ausgenommen, läuft also auch bei bereits geöffneter App.

### PDF-Erzeugung

Der PDF-Export kommt ohne externe Bibliothek aus (`Reporting/PdfWriter.cs`, rund 400 Zeilen).
Verwendet werden die PDF-Standardschriften Helvetica und Courier, die kein Einbetten erfordern.
Die Zeilenumbrüche werden mit Arial vermessen – metrisch identisch zu Helvetica, dadurch stimmt
der Umbruch exakt mit der Darstellung überein. Der Export läuft auf dem Oberflächen-Thread, weil
die Textvermessung WPF-Schriften benutzt.

---

## Aufbau

```text
src/PCHelper/
  Core/           Einstellungen, Protokoll, Win32-Aufrufe (Anzeigekonfiguration, Energie-API)
  Diagnostics/    Systemprofil, Prüfmodul, Ereignisprotokoll, Symptomkatalog, die Prüfungen
  Knowledge/      Wissensdatenbank bekannter Problemmuster
  Monitoring/     Dauerüberwachung, Messreihen, Vorfälle
  Fixes/          Katalog der Reparaturen und deren Ausführung
  Maintenance/    Wartung: Autostart, Platz schaffen, Geräte – Punkte mit Knopf, prüfen ist folgenlos
  Reporting/      Berichte als PDF (eigener Generator), HTML und Markdown
  Update/         Update über GitHub Releases
  ViewModels/     Ansichtslogik
  Views/          Oberfläche (WPF)
  Themes/         Farben und Steuerelement-Stile
knowledge/        known-issues.json (eingebettet und per HTTPS nachladbar)
.github/workflows Build und Release
```

### Wo die Daten liegen

| Zweck | Ort |
| --- | --- |
| Einstellungen | `%LOCALAPPDATA%\PCHelper\settings.json` |
| Messreihen (JSONL, täglich) | `%LOCALAPPDATA%\PCHelper\telemetry\` |
| Vorfälle | `%LOCALAPPDATA%\PCHelper\incidents\` |
| Sitzungsprotokoll (Start / Herunterfahren) | `%LOCALAPPDATA%\PCHelper\sitzungsprotokoll.txt` |
| Protokolldatei | `%LOCALAPPDATA%\PCHelper\pchelper.log` |
| Berichte (PDF, HTML, CSV) | `Dokumente\PC Helper\Berichte\` |

---

## Grenzen

- **Windows only**, x64. Getestet auf Windows 11.
- **Die Symptomsuche ist ein Wortvergleich, kein Sprachmodell.** Sie gleicht die Eingabe gegen
  hinterlegte Stichwörter ab – nachvollziehbar, sofort da und ohne Netzverbindung, aber sie versteht
  keine Umschreibungen, für die niemand ein Stichwort hinterlegt hat. Findet sie nichts, hilft die
  vollständige Prüfung weiter.
- **GPU-Sensoren nur für NVIDIA** (über `nvidia-smi`, liegt bei installiertem Treiber in
  `System32`). Bei AMD- und Intel-Grafik bleiben Temperatur und Leistungsaufnahme leer;
  alle übrigen Prüfungen laufen normal.
- **Der gemeldete Anschlusstyp stammt vom Treiber.** Manche Treiber melden eine HDMI-Verbindung
  als „DVI“. Der tatsächlich benutzte Anschluss lässt sich nur am Gerät ablesen.
- **Die App stellt keine Diagnose im medizinischen Sinne.** Sie sammelt Belege, ordnet sie ein und
  schlägt eine Reihenfolge zum Ausschließen vor. Die letzte Bestätigung liefert immer erst der
  praktische Test.

## Lizenz

MIT – siehe [LICENSE](LICENSE).
