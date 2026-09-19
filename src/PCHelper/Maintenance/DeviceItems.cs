using Microsoft.Win32;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Fixes;

namespace PCHelper.Maintenance;

/// <summary>
/// Der Ersatz fuer die alten Windows-Problembehandlungen: sieht alle Geraete durch, uebersetzt die Fehlercodes in
/// normales Deutsch und bietet den passenden Handgriff an - allen voran "neu einstecken, ohne ein Kabel anzufassen".
/// </summary>
public static class DeviceItems
{
    public static Task<IReadOnlyList<MaintItem>> ScanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<MaintItem>>(Scan, ct);

    private static IReadOnlyList<MaintItem> Scan()
    {
        var items = new List<MaintItem>();
        var devices = DeviceScanner.ReadDevices(fresh: true);

        AddProblemDevices(items, devices);
        AddDeadUsb(items);
        AddUsbPlanSetting(items);
        AddDevicePowerSaving(items);
        AddCameras(items, devices);
        return items;
    }

    // ------------------------------------------------------------------ Geraete mit Fehlercode

    private static void AddProblemDevices(List<MaintItem> items, IReadOnlyList<PnpDevice> devices)
    {
        var problems = devices.Where(d => d.ErrorCode != 0 && !DeviceScanner.IsDeadUsbEntry(d)).ToList();

        foreach (var d in problems)
        {
            var info = DeviceErrorCodes.Describe(d.ErrorCode)
                       ?? new DeviceErrorInfo($"Fehlercode {d.ErrorCode}", "Diesen Fehlercode kenne ich nicht.", "Im Geraete-Manager nachsehen.");

            // Code 45 ist der Normalfall bei allem, was man absteckt - das muss nicht als Problem erschrecken.
            var severity = d.ErrorCode == 45 ? Severity.Info
                : d.ErrorCode is 43 or 10 or 28 or 39 or 31 ? Severity.Critical
                : Severity.Warning;

            var canRestart = DeviceScanner.CanRestart(d);
            items.Add(new MaintItem
            {
                Key = $"device|problem|{d.DeviceId}",
                Title = d.Name.Length > 0 ? d.Name : d.DeviceId,
                Group = string.IsNullOrEmpty(d.DeviceClass) ? "Geraet" : d.DeviceClass,
                Value = info.Short,
                Meaning = $"{info.Meaning} (Windows-Fehlercode {d.ErrorCode})",
                Recommendation = info.Advice,
                Severity = severity,
                Weight = d.ErrorCode == 45 ? 20 : 500,
                ActionText = canRestart ? "Neu einstecken" : "",
                ActionDetail = RestartDetail,
                Execute = canRestart ? () => RestartAsync(d) : null,
            });
        }

        if (problems.Count == 0)
        {
            items.Add(new MaintItem
            {
                Key = "device|no-problems",
                Title = "Kein Geraet meldet gerade einen Fehler",
                Value = "sauber",
                Meaning = "Im Moment klemmt nirgends etwas. Wenn ein Geraet nur ab und zu ausfaellt, lass diese Pruefung genau " +
                          "dann laufen, wenn es gerade weg ist - dann steht hier, woran es liegt.",
                Severity = Severity.Ok,
                Weight = 15,
            });
        }
    }

    private const string RestartDetail =
        "Das Geraet wird per Software neu gestartet - dasselbe wie Kabel raus und wieder rein, nur ohne hinter den Rechner zu " +
        "kriechen. Es behaelt dabei seinen Anschluss, deshalb bleiben Einstellungen eher erhalten. In laufenden Programmen " +
        "muss man es eventuell einmal neu auswaehlen. Das braucht Administratorrechte.";

    /// <summary>
    /// pnputil ist der sanftere Weg, meldet aber auch dann Erfolg, wenn es gar nichts getan hat. Auf seine Rueckmeldung
    /// ist deshalb kein Verlass - am Ende zaehlt nur der nachgepruefte Zustand des Geraets.
    /// </summary>
    internal static string RestartScript(string deviceId) => $$"""
        $id = {{PowerShellRunner.Quote(deviceId)}}
        if (-not (Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue)) {
          Write-Output 'PCH-RESULT:FAIL|Das Geraet ist nicht mehr auffindbar. Vermutlich haengt es gerade wirklich nicht am Rechner - dann hilft nur das Kabel.'
          return
        }
        try { $null = & pnputil /restart-device "$id" 2>&1 } catch { }
        Start-Sleep -Seconds 3
        $jetzt = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
        if (-not $jetzt -or $jetzt.Status -ne 'OK') {
          Disable-PnpDevice -InstanceId $id -Confirm:$false
          Start-Sleep -Seconds 3
          Enable-PnpDevice -InstanceId $id -Confirm:$false
          Start-Sleep -Seconds 3
          $jetzt = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
        }
        if (-not $jetzt) { Write-Output 'PCH-RESULT:OK|GONE' } else { Write-Output ('PCH-RESULT:OK|' + $jetzt.Status) }
        """;

    private static async Task<ActionResult> RestartAsync(PnpDevice device)
    {
        var (ok, message) = await PowerShellRunner.RunElevatedAsync(RestartScript(device.DeviceId), "geraet-neu-starten");
        if (!ok) return new ActionResult(false, message);

        var name = device.Name.Length > 0 ? device.Name : device.DeviceId;
        return message switch
        {
            "OK" => new ActionResult(true,
                $"'{name}' wurde neu eingelesen und meldet sich als in Ordnung. Das entspricht Kabel raus und wieder rein. " +
                "In laufenden Programmen musst du es eventuell einmal neu auswaehlen."),
            "GONE" => new ActionResult(false,
                $"'{name}' ist nach dem Neustart nicht zurueckgekommen. Warte ein paar Sekunden und pruefe noch einmal - " +
                "manche Geraete brauchen laenger."),
            _ => new ActionResult(false,
                $"'{name}' wurde neu eingelesen, meldet aber weiterhin: {message}. Dann steckt mehr dahinter als ein hakeliger " +
                "Anschluss - schalte das USB-Stromsparen ab (steht in dieser Liste) und starte einmal richtig neu."),
        };
    }

    // ------------------------------------------------------------------ Tote USB-Anmeldungen

    private static void AddDeadUsb(List<MaintItem> items)
    {
        foreach (var d in DeviceScanner.DeadUsbEntries())
        {
            items.Add(new MaintItem
            {
                Key = $"device|dead-usb|{d.DeviceId}",
                Title = d.Name.Length > 0 ? d.Name : "Unbekanntes USB-Geraet",
                Group = "USB",
                Value = "haengengeblieben",
                Meaning = "Ein USB-Anschluss hat versucht, ein Geraet anzumelden, und ist dabei steckengeblieben. Solche Leichen " +
                          "sammeln sich an, wenn Geraete aus dem Stromsparen nicht sauber aufwachen - und sie koennen dazu " +
                          "fuehren, dass der Anschluss beim naechsten Mal gar nicht mehr will.",
                Recommendation = "Wegraeumen. Steckt das Geraet noch, meldet Windows es sofort sauber neu an.",
                Severity = Severity.Warning,
                Weight = 400,
                ActionText = "Wegraeumen",
                Confirm = true,
                ActionDetail = "Der Eintrag wird aus Windows entfernt und die Anschluesse werden neu eingelesen. " +
                               "Das braucht Administratorrechte.",
                Execute = () => RemoveAsync(d),
            });
        }
    }

    internal static string RemoveScript(string deviceId) => $$"""
        $id = {{PowerShellRunner.Quote(deviceId)}}
        try { $null = & pnputil /remove-device "$id" 2>&1 } catch { }
        try { $null = & pnputil /scan-devices 2>&1 } catch { }
        Start-Sleep -Seconds 2
        # Nicht der Rueckgabewert zaehlt, sondern ob der Eintrag danach wirklich verschwunden ist.
        if (Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue) {
          Write-Output 'PCH-RESULT:FAIL|Der Eintrag ist noch da. Solche Leichen lassen sich manchmal erst nach einem echten Neustart entfernen - probier es danach nochmal.'
        } else { Write-Output 'PCH-RESULT:OK|' }
        """;

    private static async Task<ActionResult> RemoveAsync(PnpDevice device)
    {
        var (ok, message) = await PowerShellRunner.RunElevatedAsync(RemoveScript(device.DeviceId), "usb-entfernen");
        return ok
            ? new ActionResult(true, "Weggeraeumt und die Anschluesse neu eingelesen. Was noch steckt, meldet sich innerhalb weniger Sekunden sauber neu an.")
            : new ActionResult(false, message);
    }

    // ------------------------------------------------------------------ Stromsparen

    private static void AddUsbPlanSetting(List<MaintItem> items)
    {
        // Die Nummer-eins-Ursache dafuer, dass USB-Kameras sich verabschieden.
        var value = PowerApi.ReadAcValue(PowerApi.SubUsb, PowerApi.UsbSelectiveSuspend);
        if (value is null) return;

        if (value == 1)
        {
            var fix = FixCatalog.ById("usb-suspend-off");
            items.Add(new MaintItem
            {
                Key = "device|usb-plan-saving",
                Title = "USB-Stromsparen ist eingeschaltet",
                Group = "Energieplan",
                Value = "an",
                Meaning = "Windows schaltet USB-Anschluesse ab, wenn es meint, das Geraet werde gerade nicht gebraucht. Bei Webcams, " +
                          "Mikrofonen und externen Platten ist das die haeufigste Ursache dafuer, dass sie mitten im Betrieb " +
                          "verschwinden - und dann hilft nur noch Kabel raus, Kabel rein. Der eingesparte Strom liegt im Bereich " +
                          "einer Gluehbirne, die kurz blinkt.",
                Recommendation = "Abschalten. Auf einem Rechner, der am Stromnetz haengt, bringt es ohnehin nichts.",
                Severity = Severity.Critical,
                Weight = 900,
                ActionText = "Abschalten",
                Confirm = true,
                ActionDetail = fix is null ? "" : "Es werden diese Befehle als Administrator ausgefuehrt:\n\n" + fix.CommandPreview,
                Execute = async () =>
                {
                    var result = await FixRunner.ApplyAsync(fix!);
                    if (result.ExitCode == 1223) return new ActionResult(false, "Die Administrator-Abfrage wurde abgebrochen - es wurde nichts geaendert.");
                    var now = PowerApi.ReadAcValue(PowerApi.SubUsb, PowerApi.UsbSelectiveSuspend);
                    return now == 0
                        ? new ActionResult(true, "USB-Stromsparen abgeschaltet, fuer Netz- und Akkubetrieb. Wirkt sofort fuer alles, was " +
                                                 "ab jetzt eingesteckt wird - fuer bereits laufende Geraete ab dem naechsten Neustart.")
                        : new ActionResult(false, "Die Einstellung steht weiterhin auf \"an\". " + result.Combined.Trim());
                },
            });
        }
        else
        {
            items.Add(new MaintItem
            {
                Key = "device|usb-plan-saving",
                Title = "USB-Stromsparen",
                Group = "Energieplan",
                Value = "aus",
                Meaning = "Windows schaltet deine USB-Anschluesse nicht eigenmaechtig ab. Genau richtig fuer Kameras und Mikrofone.",
                Severity = Severity.Ok,
                Weight = 10,
            });
        }
    }

    private static void AddDevicePowerSaving(List<MaintItem> items)
    {
        // Das Haekchen sitzt pro Geraet und ist unabhaengig vom Energieplan.
        var saving = DeviceScanner.PowerSavingDevices(out var readable, fresh: true);

        if (!readable)
        {
            items.Add(new MaintItem
            {
                Key = "device|power-saving-unreadable",
                Title = "Stromsparen einzelner Geraete",
                Value = "nicht lesbar",
                Meaning = "Diese Einstellung laesst sich mit normalen Benutzerrechten nicht immer lesen. Mit einem als Administrator " +
                          "gestarteten PC Helper steht hier mehr.",
                Severity = Severity.Info,
                Weight = 5,
            });
            return;
        }

        var changeable = DeviceScanner.PowerSavingDevices(out _, onlyChangeable: true);
        if (saving.Count == 0)
        {
            items.Add(new MaintItem
            {
                Key = "device|power-saving",
                Title = "Stromsparen einzelner Geraete",
                Value = "aus",
                Meaning = "Kein Geraet darf zum Stromsparen abgeschaltet werden. Vorbildlich.",
                Severity = Severity.Ok,
                Weight = 10,
            });
            return;
        }

        if (changeable.Count == 0) return;

        var names = string.Join(", ", changeable.Select(DeviceScanner.ShortName).Distinct().Take(6));
        items.Add(new MaintItem
        {
            Key = "device|power-saving",
            Title = "Windows darf einzelne Geraete abschalten",
            Group = "Geraete-Manager",
            Value = $"{changeable.Count} Geraete",
            Meaning = "Bei diesen Geraeten ist im Geraete-Manager das Haekchen \"Computer kann das Geraet ausschalten, um Energie zu " +
                      "sparen\" gesetzt. Das ist eine zweite, davon unabhaengige Stromspar-Ebene neben dem Energieplan - deshalb " +
                      $"bringt es oft nichts, nur den Energieplan umzustellen. Betroffen sind unter anderem: {names}.",
            Recommendation = "Fuer USB-Verteiler und Netzwerkkarten abschalten. Genau das verhindert, dass Kamera oder Mikrofon " +
                             "mitten im Betrieb wegbrechen.",
            Severity = Severity.Warning,
            Weight = 850,
            ActionText = "Stromsparen abschalten",
            Confirm = true,
            ActionDetail = "Bei USB-Verteilern, USB-Geraeten und Netzwerkkarten wird das Haekchen entfernt. " +
                           "Tonchips bleiben unberuehrt. Das braucht Administratorrechte.",
            Execute = DisablePowerSavingAsync,
        });
    }

    // Die Abfrage bricht bei einzelnen Geraeten mit "Ungueltiges Objekt" ab. Mit SilentlyContinue bleibt das, was bis dahin
    // gelesen wurde, brauchbar - und am Ende zaehlt ohnehin nur die Nachpruefung.
    internal const string PowerSavingScript = """
        $pattern = '^(USB\\ROOT_HUB|USB\\VID_|PCI\\VEN_.*NET)'
        $n = 0
        foreach ($d in @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object { $_.Enable -and $_.InstanceName -match $pattern })) {
          try { $d.Enable = $false; Set-CimInstance -InputObject $d; $n++ } catch { }
        }
        $rest = @(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object { $_.Enable -and $_.InstanceName -match $pattern }).Count
        if ($n -eq 0) { Write-Output 'PCH-RESULT:FAIL|Keine der Einstellungen liess sich aendern.' }
        elseif ($rest -gt 0) { Write-Output ('PCH-RESULT:FAIL|Bei ' + $rest + ' Geraeten steht das Haekchen weiterhin.') }
        else { Write-Output ('PCH-RESULT:OK|' + $n) }
        """;

    private static async Task<ActionResult> DisablePowerSavingAsync()
    {
        var (ok, message) = await PowerShellRunner.RunElevatedAsync(PowerSavingScript, "geraete-stromsparen");
        return ok
            ? new ActionResult(true, $"Bei {message} Geraeten darf Windows jetzt nicht mehr eigenmaechtig den Strom abdrehen. Genau das " +
                                     "ist der Handgriff, der Kameras und Mikrofone davon abhaelt, mitten im Betrieb zu verschwinden.")
            : new ActionResult(false, message);
    }

    // ------------------------------------------------------------------ Kameras

    private static void AddCameras(List<MaintItem> items, IReadOnlyList<PnpDevice> devices)
    {
        var cameras = devices.Where(d => d.DeviceClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) && d.Name.Length > 0).ToList();

        foreach (var cam in cameras)
        {
            var real = cam.IsUsb;
            bool healthy = cam.ErrorCode == 0 && cam.Status.Equals("OK", StringComparison.OrdinalIgnoreCase);

            items.Add(new MaintItem
            {
                Key = $"device|camera|{cam.DeviceId}",
                Title = cam.Name,
                Group = "Kamera",
                Value = healthy ? "laeuft" : cam.Status,
                Meaning = real
                    ? "Eine echte Kamera am USB-Anschluss. Der Knopf startet sie neu - dasselbe wie Kabel raus und wieder rein, " +
                      "nur ohne dass du hinter den Rechner kriechen musst."
                    : "Eine virtuelle Kamera - kein echtes Geraet, sondern ein Programm, das sich als Kamera ausgibt (etwa um ein " +
                      "Handy als Webcam zu benutzen).",
                Severity = healthy ? Severity.Ok : Severity.Critical,
                Weight = real ? 800 : 100,
                ActionText = real ? "Neu einstecken" : "",
                ActionDetail = RestartDetail,
                Execute = real ? () => RestartAsync(cam) : null,
            });
        }

        // Mehrere Kameras: Programme greifen gern zur falschen.
        if (cameras.Count > 1)
        {
            items.Add(new MaintItem
            {
                Key = "device|multiple-cameras",
                Title = "Mehrere Kameras angemeldet",
                Value = cameras.Count.ToString(),
                Meaning = $"Es sind {cameras.Count} Kameras eingetragen: {string.Join(", ", cameras.Select(c => c.Name))}. Programme greifen " +
                          "sich beim Start oft einfach die erste in der Liste - wenn ein Programm \"die Kamera geht nicht\" meldet, hat es " +
                          "womoeglich nur die falsche erwischt.",
                Recommendation = "Im Programm selbst (OBS, Discord, Teams) nachsehen, welche Kamera ausgewaehlt ist.",
                Severity = Severity.Info,
                Weight = 90,
            });
        }

        var users = DeviceScanner.CameraUsers();
        if (users.Count > 0)
        {
            items.Add(new MaintItem
            {
                Key = "device|camera-in-use",
                Title = "Die Kamera ist gerade in Benutzung",
                Value = $"{users.Count} Programm(e)",
                Meaning = $"Diese Programme haben die Kamera zurzeit belegt: {string.Join(", ", users)}. Eine Webcam kann meist nur von " +
                          "einem Programm gleichzeitig benutzt werden - wenn ein zweites \"kein Bild\" zeigt, ist das oft der ganze Grund.",
                Recommendation = "Das andere Programm schliessen, dann geht die Kamera wieder.",
                Severity = Severity.Info,
                Weight = 300,
            });
        }

        // Kamera-Datenschutz: Windows verbietet den Zugriff, und kein Programm zeigt einen Fehler.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam");
            var value = key?.GetValue("Value") as string;
            if (value is not null && !value.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                var fix = FixCatalog.ById("camera-privacy-allow");
                items.Add(new MaintItem
                {
                    Key = "device|camera-privacy",
                    Title = "Kamerazugriff gesperrt",
                    Group = "Datenschutz",
                    Value = value,
                    Meaning = "Windows verbietet den Zugriff auf die Kamera. Dann bleibt das Bild schwarz, ohne dass irgendein Programm " +
                              "einen Fehler zeigt - und man sucht sich dumm und daemlich an Treibern und Kabeln.",
                    Recommendation = "Wieder erlauben.",
                    Severity = Severity.Critical,
                    Weight = 950,
                    ActionText = "Erlauben",
                    ActionDetail = fix is null ? "" : "Es werden diese Befehle ausgefuehrt (nur fuer dein Benutzerkonto, ohne Administratorrechte):\n\n" + fix.CommandPreview,
                    Execute = async () =>
                    {
                        var result = await FixRunner.ApplyAsync(fix!);
                        return result.Success
                            ? new ActionResult(true, "Kamerazugriff wieder erlaubt.")
                            : new ActionResult(false, result.Combined.Trim());
                    },
                });
            }
        }
        catch { /* Datenschutz nicht lesbar: dann kein Befund */ }
    }
}
