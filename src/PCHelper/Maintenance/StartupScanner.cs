using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Maintenance;

/// <summary>
/// Zeigt alles, was beim Hochfahren ungefragt mitstartet - auch die Wege, die der Task-Manager verschweigt:
/// Registry, Autostart-Ordner, Aufgabenplanung und fremde Dienste. Ausschalten ist reversibel, das Programm
/// bleibt installiert.
/// </summary>
public static class StartupScanner
{
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    // Machine, Registry-Pfad, Unterschluessel unter StartupApproved, Beschriftung.
    private static readonly (bool Machine, string Path, string Approved, string Scope)[] RunKeys =
    {
        (false, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "nur fuer dich"),
        (true, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "fuer alle Benutzer"),
        (true, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "fuer alle Benutzer, 32-Bit"),
    };

    private static readonly Regex KnownVendors =
        new("Microsoft|Windows|Realtek|Intel|NVIDIA|AMD", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Updater =
        new("update", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<IReadOnlyList<MaintItem>> ScanAsync(Settings settings, CancellationToken ct = default)
    {
        var items = new List<MaintItem>();

        await Task.Run(() =>
        {
            ScanRegistry(items);
            ScanFolders(items);
            ScanServices(items, settings);
        }, ct);

        items.AddRange(await ScanTasksAsync(ct));
        return items;
    }

    // ------------------------------------------------------------------ Registry

    private static void ScanRegistry(List<MaintItem> items)
    {
        foreach (var (machine, path, approved, scope) in RunKeys)
        {
            try
            {
                using var key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(path);
                if (key is null) continue;

                foreach (var name in key.GetValueNames())
                {
                    // Der eigene Eintrag gehoert zu den Einstellungen dieser App und wird dort geschaltet.
                    if (string.IsNullOrEmpty(name) || name.Equals("PCHelper", StringComparison.OrdinalIgnoreCase)) continue;
                    var command = key.GetValue(name)?.ToString() ?? "";

                    items.Add(BuildEntry(
                        key: $"startup|reg|{(machine ? "HKLM" : "HKCU")}|{approved}|{name}",
                        title: name, group: $"Registry, {scope}", meaningPrefix: "",
                        command: command, machine: machine, approved: approved, valueName: name));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Autostart-Schluessel {path} nicht lesbar: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ Autostart-Ordner

    private static void ScanFolders(List<MaintItem> items)
    {
        var folders = new (string Path, bool Machine, string Scope)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false, "nur fuer dich"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true, "fuer alle Benutzer"),
        };

        foreach (var (path, machine, scope) in folders)
        {
            try
            {
                if (!Directory.Exists(path)) continue;

                foreach (var file in Directory.EnumerateFiles(path))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    items.Add(BuildEntry(
                        key: $"startup|folder|{(machine ? "HKLM" : "HKCU")}|{fileName}",
                        title: Path.GetFileNameWithoutExtension(fileName), group: "Autostart-Ordner",
                        meaningPrefix: $"Liegt als Verknuepfung im Autostart-Ordner ({scope}). ",
                        command: file, machine: machine, approved: "StartupFolder", valueName: fileName));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Autostart-Ordner {path} nicht lesbar: {ex.Message}");
            }
        }
    }

    private static MaintItem BuildEntry(
        string key, string title, string group, string meaningPrefix,
        string command, bool machine, string approved, string valueName)
    {
        bool enabled = IsEnabled(machine, approved, valueName);
        var info = StartupKnowledge.Rate(title);
        var fileInfo = StartupKnowledge.DescribeFile(command);

        var meaning = meaningPrefix + (enabled ? "" : "Startet zurzeit nicht mit. ") + info.Text;
        if (fileInfo is not null) meaning += $" Laut Programmdatei: {fileInfo}.";

        var severity = !enabled ? Severity.Ok : info.Advice switch
        {
            StartupAdvice.Remove => Severity.Warning,
            StartupAdvice.Keep => Severity.Ok,
            _ => Severity.Info,
        };

        var recommendation = !enabled ? "" : info.Advice switch
        {
            StartupAdvice.Remove => "Kann raus. Das Programm bleibt installiert und laesst sich jederzeit normal starten.",
            StartupAdvice.Keep => "Ich wuerde es dranlassen - der Knopf ist trotzdem da, falls du es anders siehst. " +
                                  "Rueckgaengig geht jederzeit.",
            _ => "",
        };

        return new MaintItem
        {
            Key = key,
            Title = title,
            Group = group,
            Value = enabled ? "startet mit" : "aus",
            Meaning = meaning,
            Recommendation = recommendation,
            Severity = severity,
            Weight = !enabled ? 0 : info.Advice == StartupAdvice.Remove ? 10 : 5,
            ActionText = enabled ? "Rauswerfen" : "Wieder einschalten",
            Warning = enabled && info.Advice == StartupAdvice.Keep ? "Davon rate ich ab:\n\n" + info.Text : "",
            WebSearch = info.Known ? "" : $"{fileInfo ?? title} Windows Autostart brauche ich das",
            ActionDetail = machine
                ? "Der Eintrag gilt fuer alle Benutzer. Dafuer braucht es Administratorrechte."
                : "Der Eintrag gilt nur fuer dein Benutzerkonto.",
            Execute = () => SetApprovedAsync(machine, approved, valueName, title, enable: !enabled),
        };
    }

    /// <summary>
    /// Liest den Ein/Aus-Zustand, den auch der Task-Manager verwaltet (StartupApproved).
    /// Erstes Byte gerade = aktiv, ungerade = vom Benutzer abgeschaltet. Kein Eintrag heisst: aktiv.
    /// </summary>
    private static bool IsEnabled(bool machine, string approved, string valueName)
    {
        // Genau wie der Task-Manager: Eintraege fuer alle Benutzer haben ihren Zustand in HKLM, eigene Eintraege in HKCU.
        // (Auf einem echten System liegt z. B. der Zustand von HKLM\...\Run unter HKLM\...\StartupApproved\Run,
        // der von Wow6432Node\...\Run unter ...\StartupApproved\Run32.)
        try
        {
            using var key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey($@"{ApprovedRoot}\{approved}");
            if (key?.GetValue(valueName) is byte[] { Length: > 0 } data)
                return (data[0] & 1) == 0;
        }
        catch { /* nicht lesbar: gilt als aktiv */ }

        return true;
    }

    private static async Task<ActionResult> SetApprovedAsync(
        bool machine, string approved, string valueName, string title, bool enable)
    {
        // Eintraege fuer alle Benutzer liegen in HKLM und brauchen Administratorrechte, eigene Eintraege in HKCU.
        if (machine && !Shell.IsElevated)
        {
            var (ok, message) = await PowerShellRunner.RunElevatedAsync(ApprovedScript(approved, valueName, enable), "autostart");
            if (!ok) return new ActionResult(false, message);
        }
        else
        {
            var error = WriteApproved(machine ? Registry.LocalMachine : Registry.CurrentUser, approved, valueName, enable);
            if (error is not null) return new ActionResult(false, "Die Einstellung liess sich nicht schreiben: " + error);
        }

        // Es zaehlt nur, was danach wirklich drinsteht.
        if (IsEnabled(machine, approved, valueName) != enable)
            return new ActionResult(false, "Die Umstellung ist nicht angekommen - der Eintrag steht weiterhin auf dem alten Wert.");

        return new ActionResult(true, enable
            ? $"Wieder eingeschaltet. '{title}' startet ab dem naechsten Hochfahren wieder mit."
            : $"Rausgeworfen. '{title}' startet ab dem naechsten Hochfahren nicht mehr mit - das Programm selbst bleibt installiert.");
    }

    /// <summary>Schreibt den Zustand so, wie ihn auch der Task-Manager ablegt. Liefert die Fehlermeldung oder null.</summary>
    private static string? WriteApproved(RegistryKey hive, string approved, string valueName, bool enable)
    {
        try
        {
            using var key = hive.CreateSubKey($@"{ApprovedRoot}\{approved}", writable: true);

            var data = new byte[12];
            data[0] = (byte)(enable ? 2 : 3);
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(valueName, data, RegistryValueKind.Binary);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Schreibt den Ein/Aus-Zustand fuer Eintraege, die fuer alle Benutzer gelten (HKLM, braucht Administratorrechte).</summary>
    internal static string ApprovedScript(string approved, string valueName, bool enable) =>
        $"$p = 'HKLM:\\{ApprovedRoot}\\{approved}'\n" +
        "if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }\n" +
        "$b = New-Object byte[] 12\n" +
        $"$b[0] = {(enable ? 2 : 3)}\n" +
        $"Set-ItemProperty -Path $p -Name {PowerShellRunner.Quote(valueName)} -Value $b -Type Binary\n" +
        "Write-Output 'PCH-RESULT:OK|'\n";

    // ------------------------------------------------------------------ Aufgabenplanung

    private sealed record TaskRow(string Name, string Path, bool Enabled, string Exec);

    /// <summary>
    /// Der Weg, den fast niemand kennt: Viele Programme verstecken ihren Autostart in der Aufgabenplanung,
    /// weil er im Task-Manager nicht auftaucht. Abgeschaltete Aufgaben werden mitgelistet, damit sich das umkehren laesst.
    /// </summary>
    private static async Task<IReadOnlyList<MaintItem>> ScanTasksAsync(CancellationToken ct)
    {
        const string script =
            "$rows = @(Get-ScheduledTask | Where-Object {\n" +
            "  $_.TaskPath -notlike '\\Microsoft\\*' -and\n" +
            "  ($_.Triggers | Where-Object { $_.CimClass.CimClassName -match 'LogonTrigger|BootTrigger' })\n" +
            "} | ForEach-Object {\n" +
            "  [pscustomobject]@{ Name = $_.TaskName; Path = $_.TaskPath; Enabled = ($_.State -ne 'Disabled');\n" +
            "    Exec = [string](($_.Actions | Select-Object -First 1).Execute) }\n" +
            "})\n" +
            "ConvertTo-Json -InputObject $rows -Compress";

        var result = await PowerShellRunner.RunAsync(script, 60_000, ct);
        var items = new List<MaintItem>();
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            Log.Warn("Aufgabenplanung nicht lesbar: " + result.Combined.Trim());
            return items;
        }

        List<TaskRow> rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<TaskRow>>(result.StdOut.Trim(),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException ex)
        {
            Log.Warn("Aufgabenplanung: Antwort nicht lesbar: " + ex.Message);
            return items;
        }

        foreach (var t in rows)
        {
            bool sensitive = StartupKnowledge.Sensitive.IsMatch(t.Name);
            bool updater = !sensitive && Updater.IsMatch(t.Name);
            var fileInfo = StartupKnowledge.DescribeFile(t.Exec);

            string meaning, recommendation, warning = "";
            Severity severity;
            int weight;

            if (!t.Enabled)
            {
                severity = Severity.Ok; weight = 0;
                meaning = "Ist abgeschaltet und startet nicht mehr mit. Die Aufgabe selbst besteht weiter.";
                recommendation = "";
            }
            else if (sensitive)
            {
                severity = Severity.Ok; weight = 3;
                meaning = "Startet beim Anmelden ueber die Aufgabenplanung - ein Weg, den der Task-Manager nicht zeigt. " +
                          "Dem Namen nach gehoert das zu deiner Hardware oder zum Schutz des Systems " +
                          "(Luefter, Treiber, Virenschutz, Sicherung).";
                recommendation = "Ich wuerde es anlassen. Wenn du weisst, dass du es nicht brauchst, kannst du es trotzdem " +
                                 "abschalten - die Aufgabe bleibt bestehen und ist jederzeit wieder einschaltbar.";
                warning = "Der Name deutet auf Hardware oder Systemschutz hin.\n\nEine abgeschaltete Lueftersteuerung kann " +
                          "dazu fuehren, dass dein Rechner heiss wird. Bei Virenschutz oder Anti-Cheat kann Software danach " +
                          "den Dienst verweigern.\n\nWenn du dieses Programm kennst und weisst, dass du es nicht brauchst, " +
                          "ist das voellig in Ordnung.";
            }
            else if (updater)
            {
                severity = Severity.Warning; weight = 8;
                meaning = "Sucht regelmaessig nach Updates fuer ein Programm - ueber die Aufgabenplanung, die im Task-Manager " +
                          "nicht auftaucht. Solche Sucher laufen oft mehrmals taeglich los und kosten jedes Mal Rechenzeit und Leitung.";
                recommendation = "Kann abgeschaltet werden. Das Programm laeuft weiter, aktualisiert sich nur nicht mehr von " +
                                 "selbst - du musst dann gelegentlich selbst nachsehen.";
            }
            else
            {
                severity = Severity.Info; weight = 6;
                meaning = "Startet automatisch beim Anmelden oder Hochfahren - ueber die Aufgabenplanung. Diesen Weg zeigt der " +
                          $"Task-Manager NICHT, deshalb uebersieht man ihn leicht. Ablage: {t.Path}";
                recommendation = "Wenn du erkennst, wozu das gehoert, und es nicht bei jedem Start brauchst: abschalten. " +
                                 "Wenn du unsicher bist, lass es lieber an - die Aufgabe bleibt bestehen und ist jederzeit " +
                                 "wieder einschaltbar.";
            }

            if (fileInfo is not null) meaning += $" Laut Programmdatei: {fileInfo}.";

            var enable = !t.Enabled;
            items.Add(new MaintItem
            {
                Key = $"startup|task|{t.Path}{t.Name}",
                Title = t.Name,
                Group = "Aufgabenplanung",
                Value = t.Enabled ? "startet mit" : "aus",
                Meaning = meaning,
                Recommendation = recommendation,
                Severity = severity,
                Weight = weight,
                ActionText = enable ? "Wieder einschalten" : "Abschalten",
                Warning = warning,
                WebSearch = fileInfo is null && !sensitive ? $"{t.Name} Windows geplante Aufgabe wofuer" : "",
                ActionDetail = "Die Aufgabe wird ueber die Aufgabenplanung umgestellt. Dafuer braucht es Administratorrechte.",
                Execute = () => SetTaskAsync(t, enable),
            });
        }

        return items;
    }

    internal static string TaskScript(string taskName, string taskPath, bool enable)
    {
        var name = PowerShellRunner.Quote(taskName);
        var path = PowerShellRunner.Quote(taskPath);
        return
            $"{(enable ? "Enable" : "Disable")}-ScheduledTask -TaskName {name} -TaskPath {path} | Out-Null\n" +
            $"$state = [string](Get-ScheduledTask -TaskName {name} -TaskPath {path}).State\n" +
            $"if (({(enable ? "$state -ne 'Disabled'" : "$state -eq 'Disabled'")})) {{ Write-Output 'PCH-RESULT:OK|' }}\n" +
            "else { Write-Output ('PCH-RESULT:FAIL|Die Aufgabe steht weiterhin auf: ' + $state) }\n";
    }

    private static async Task<ActionResult> SetTaskAsync(TaskRow task, bool enable)
    {
        var (ok, message) = await PowerShellRunner.RunElevatedAsync(TaskScript(task.Name, task.Path, enable), "aufgabe");
        if (!ok) return new ActionResult(false, message);

        return new ActionResult(true, enable
            ? "Wieder eingeschaltet. Die Aufgabe startet beim naechsten Anmelden wieder."
            : "Abgeschaltet. Die Aufgabe bleibt bestehen und laesst sich hier oder in der Aufgabenplanung jederzeit wieder einschalten.");
    }

    // ------------------------------------------------------------------ Dienste

    /// <summary>
    /// Dienste starten voellig unabhaengig von jeder Autostart-Liste. Fremde Dienste auf "Automatisch" sind der
    /// dritte, meist uebersehene Startweg - und der einzige, der auch dann laeuft, wenn niemand angemeldet ist.
    /// </summary>
    private static void ScanServices(List<MaintItem> items, Settings settings)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var rows = Wmi.Query("SELECT Name, DisplayName, StartMode, PathName FROM Win32_Service");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var name = row.Str("Name");
            var display = row.Str("DisplayName");
            var pathName = row.Str("PathName");
            var mode = row.Str("StartMode");
            if (name.Length == 0) continue;

            if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (pathName.Length == 0 || pathName.TrimStart('"').StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (KnownVendors.IsMatch(name)) continue;
                seen.Add(name);
                items.Add(BuildService(name, display, pathName, settings));
            }
            else if (settings.ManualizedServices.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(name);
                items.Add(BuildRestorableService(name, display, settings));
            }
        }

        // Ein Dienst, den es nicht mehr gibt, muss nicht weiter gemerkt werden.
        if (settings.ManualizedServices.RemoveAll(s => !seen.Contains(s)) > 0) settings.Save();
    }

    private static MaintItem BuildService(string name, string display, string pathName, Settings settings)
    {
        var title = display.Length > 0 ? display : name;
        bool sensitive = StartupKnowledge.Sensitive.IsMatch(name) || StartupKnowledge.Sensitive.IsMatch(display);
        bool updater = !sensitive && Updater.IsMatch(name);
        var fileInfo = StartupKnowledge.DescribeFile(pathName);

        string meaning, recommendation, warning = "";
        Severity severity;
        int weight;

        if (sensitive)
        {
            severity = Severity.Ok; weight = 1;
            meaning = "Laeuft dauerhaft im Hintergrund. Dem Namen nach gehoert das zu deiner Hardware oder zum Schutz des Systems.";
            recommendation = "Ich wuerde ihn anlassen. Der Knopf ist trotzdem da - \"Manuell\" heisst nicht aus, sondern nur: " +
                             "startet erst, wenn er gebraucht wird.";
            warning = "Der Name deutet auf Hardware oder Systemschutz hin.\n\nBei Lueftersteuerung, Treibern oder Virenschutz " +
                      "kann das dazu fuehren, dass Geraete nach dem naechsten Neustart nicht mehr richtig arbeiten.\n\n" +
                      "Rueckgaengig geht jederzeit: hier ueber \"Wieder auf Automatisch\" oder ueber die Dienste-Verwaltung von Windows.";
        }
        else
        {
            severity = updater ? Severity.Warning : Severity.Info;
            weight = updater ? 6 : 2;
            meaning = (updater
                          ? "Sucht dauerhaft im Hintergrund nach Programm-Updates. Kann meist auf \"Manuell\" - dann startet er nur bei Bedarf."
                          : "Laeuft dauerhaft im Hintergrund, egal was in der Autostart-Liste steht.") +
                      " Dienste sind der dritte Startweg neben Registry und Autostart-Ordner - und der einzige, der auch dann " +
                      "laeuft, wenn niemand angemeldet ist.";
            recommendation = "Auf \"Manuell\" stellen ist bei fremden Diensten in aller Regel gefahrlos. Das Programm " +
                             "funktioniert weiter, der Dienst startet nur noch, wenn er wirklich gebraucht wird.";
        }

        if (fileInfo is not null) meaning += $" Laut Programmdatei: {fileInfo}.";

        return new MaintItem
        {
            Key = $"startup|service|{name}",
            Title = title,
            Group = "Dienst",
            Value = "startet automatisch",
            Meaning = meaning,
            Recommendation = recommendation,
            Severity = severity,
            Weight = weight,
            ActionText = "Auf Manuell stellen",
            Warning = warning,
            WebSearch = fileInfo is null && !sensitive ? $"{name} Windows Dienst wofuer" : "",
            ActionDetail = "Der Starttyp des Dienstes wird geaendert. Dafuer braucht es Administratorrechte.",
            Execute = () => SetServiceAsync(name, settings, automatic: false),
        };
    }

    private static MaintItem BuildRestorableService(string name, string display, Settings settings)
    {
        var title = display.Length > 0 ? display : name;
        return new MaintItem
        {
            Key = $"startup|service|{name}",
            Title = title,
            Group = "Dienst",
            Value = "manuell (von PC Helper)",
            Meaning = "Diesen Dienst hat PC Helper auf \"Manuell\" gestellt. Er startet nur noch, wenn er gebraucht wird.",
            Severity = Severity.Ok,
            ActionText = "Wieder auf Automatisch",
            ActionDetail = "Der Starttyp des Dienstes wird geaendert. Dafuer braucht es Administratorrechte.",
            Execute = () => SetServiceAsync(name, settings, automatic: true),
        };
    }

    /// <summary>
    /// Manche Programme geben ihrem Dienst eigene Rechte, in denen selbst Administratoren das Recht zum
    /// Umkonfigurieren fehlt. Dann sperrt sich der Dienst gegen jeden ausser sich selbst - aber der
    /// Registry-Schluessel gehoert weiterhin den Administratoren. Am Ende zaehlt nur, was drinsteht.
    /// </summary>
    internal static string ServiceScript(string serviceName, bool automatic)
    {
        var start = automatic ? 2 : 3;
        var type = automatic ? "Automatic" : "Manual";

        return
            $"$n = {PowerShellRunner.Quote(serviceName)}\n" +
            "$reg = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\$n\"\n" +
            "$via = ''\n" +
            "try { Set-Service -Name $n -StartupType " + type + " }\n" +
            "catch {\n" +
            "  try { Set-ItemProperty -Path $reg -Name Start -Value " + start + " -Type DWord; $via = 'registry' }\n" +
            "  catch { Write-Output ('PCH-RESULT:FAIL|Der Dienst laesst sich nicht umstellen: ' + $_.Exception.Message); return }\n" +
            "}\n" +
            "$jetzt = (Get-ItemProperty -Path $reg -Name Start).Start\n" +
            "if ($jetzt -eq " + start + ") { Write-Output ('PCH-RESULT:OK|' + $via) }\n" +
            "else { Write-Output ('PCH-RESULT:FAIL|Die Umstellung ist nicht angekommen - der Starttyp steht weiterhin auf ' + $jetzt) }\n";
    }

    private static async Task<ActionResult> SetServiceAsync(string name, Settings settings, bool automatic)
    {
        var (ok, message) = await PowerShellRunner.RunElevatedAsync(ServiceScript(name, automatic), "dienst");
        if (!ok) return new ActionResult(false, message);

        if (automatic)
            settings.ManualizedServices.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        else if (!settings.ManualizedServices.Contains(name, StringComparer.OrdinalIgnoreCase))
            settings.ManualizedServices.Add(name);
        settings.Save();

        var text = automatic
            ? "Wieder auf 'Automatisch' gestellt. Der Dienst startet ab dem naechsten Hochfahren wieder von selbst."
            : "Auf 'Manuell' gestellt. Der Dienst startet ab dem naechsten Hochfahren nur noch, wenn er gebraucht wird. " +
              "Er laeuft gerade noch weiter - das legt sich beim Neustart.";
        if (message == "registry")
            text += " Dieser Dienst hatte sich gegen das normale Umstellen gesperrt, deshalb bin ich ueber die Windows-Registrierung " +
                    "gegangen. Hinweis: Programme aus dem Microsoft Store setzen das bei einem Update manchmal zurueck.";
        return new ActionResult(true, text);
    }
}
