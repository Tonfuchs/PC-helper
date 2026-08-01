# PC Helper

**Diagnose- und Überwachungswerkzeug für sporadische Windows-Probleme** – besonders für den Fall
„der Bildschirm wird einfach schwarz, der Ton läuft aber weiter“.

Solche Fehler sind schwer zu fassen, weil sie keine Spuren hinterlassen: Kein Bluescreen, kein
Absturzbericht, im Ereignisprotokoll oft gar nichts. PC Helper geht das von zwei Seiten an:

1. **Diagnose** – eine gründliche Einmalprüfung von Hardware, Treibern, Energieeinstellungen und
   Ereignisprotokoll, verdichtet zu einer nachvollziehbaren Verdachtsreihenfolge.
2. **Dauerüberwachung** – zeichnet im Hintergrund fortlaufend Messwerte auf, damit nach einem
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

### Reparaturen

Der Bereich *Reparaturen* enthält nachvollziehbare Systemänderungen – Schnellstart abschalten,
Bildschirm-Timeout aus, PCIe-Energieverwaltung aus, hardwarebeschleunigte GPU-Planung aus, und
weitere. Dabei gilt:

- **Nichts passiert von allein.** Jede Änderung erfordert eine ausdrückliche Bestätigung.
- **Der genaue Befehl steht immer dabei**, bevor er ausgeführt wird.
- **Fast alles ist umkehrbar** – mit einem Klick auf *Zurücknehmen*.
- Auf Wunsch wird vor der ersten Änderung ein **Systemwiederherstellungspunkt** angelegt.

### Datenschutz

Alle Daten bleiben auf dem Rechner (`%LOCALAPPDATA%\PCHelper`, Berichte unter
`Dokumente\PC Helper\Berichte`). Nach außen gehen ausschließlich zwei Abrufe an GitHub:
die Update-Prüfung und das Nachladen der Wissensdatenbank. Es wird nichts hochgeladen.

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
    "processNameContains": ["iCUE"]
  },
  "summary": "Ein Satz für die Übersicht.",
  "detail": "Ausführliche Erklärung.",
  "recommendation": "Was konkret zu tun ist.",
  "causes": { "GpuDriver": 0.4, "DisplayLink": 0.6 },
  "links": [{ "label": "Quelle", "url": "https://..." }]
}
```

Gültige Ursachenbereiche für `causes`: `GpuDriver`, `DisplayLink`, `Memory`, `PowerSupply`,
`PowerSettings`, `Thermal`, `Storage`, `Software`, `Bios`, `OperatingSystem`.
Der CI-Workflow prüft bei jedem Push, ob die Datei gültiges JSON ist und die IDs eindeutig sind.

### Eine neue Prüfung ergänzen

1. Klasse anlegen, die `ICheck` implementiert (Vorlagen in
   [`src/PCHelper/Diagnostics/Checks/`](src/PCHelper/Diagnostics/Checks/)).
2. In `CheckEngine.All()` registrieren – die Reihenfolge bestimmt die Anzeige.

Ein `Finding` trägt neben Titel und Text eine Gewichtung auf Ursachenbereiche (`Causes`).
Daraus errechnet die `SuspicionEngine` die Verdachtsreihenfolge; der Schweregrad bestimmt,
wie stark ein Befund zählt. Befunde mit `Severity.Ok` erhöhen keinen Verdacht.

### Eine neue Reparatur ergänzen

Eintrag in `FixCatalog.All` anlegen: Titel, Beschreibung, Begründung, die auszuführenden Befehle und
möglichst die Befehle zur Rücknahme. Alles Weitere – UAC-Abfrage, Protokollierung, Anzeige des
Befehls vor der Ausführung – erledigt die Oberfläche automatisch.

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

Führt eine vollständige Diagnose ohne Oberfläche aus, schreibt HTML- und PDF-Bericht, protokolliert
alle Befunde nach `%LOCALAPPDATA%\PCHelper\pchelper.log` und beendet sich mit Exitcode 0 (bzw. 1 bei
Fehler). Wird auch im CI-Workflow als Rauchtest verwendet.

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
  Diagnostics/    Systemprofil, Prüfmodul, Ereignisprotokoll, die einzelnen Prüfungen
  Knowledge/      Wissensdatenbank bekannter Problemmuster
  Monitoring/     Dauerüberwachung, Messreihen, Vorfälle
  Fixes/          Katalog der Reparaturen und deren Ausführung
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
