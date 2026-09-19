using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using PCHelper.Core;
using PCHelper.Knowledge;
using PCHelper.Monitoring;
using PCHelper.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PCHelper;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private MonitorService? _monitor;
    private Settings _settings = new();
    private bool _isSelfTest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isSelfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        _isSelfTest = isSelfTest;

        // Nur eine Instanz - sonst laufen zwei Ueberwachungen parallel.
        // Der kopflose Selbsttest ist davon ausgenommen: er darf auch dann
        // laufen, wenn die App bereits im Infobereich aktiv ist.
        if (!isSelfTest)
        {
            _singleInstance = new Mutex(initiallyOwned: true, "PCHelper.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("PC Helper laeuft bereits. Das Symbol findet sich im Infobereich der Taskleiste.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unbehandelter Fehler", args.ExceptionObject as Exception);

        Log.Info($"=== {AppInfo.Name} {AppInfo.VersionDisplay} gestartet ===");

        _settings = Settings.Load();

        // Kopfloser Modus: fuehrt eine vollstaendige Diagnose aus, schreibt den
        // Bericht und beendet sich. Praktisch fuer Fernwartung und Rauchtests.
        if (isSelfTest)
        {
            _ = RunSelfTestAsync();
            return;
        }

        var knowledge = KnowledgeBase.LoadEmbedded();
        var incidents = new IncidentStore();
        _monitor = new MonitorService(_settings, incidents);

        // Rueckfallebene: Windows meldet das Sitzungsende ueber zwei Wege.
        // RecordShutdown ist idempotent, doppeltes Auslesen schadet also nicht.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, args) =>
        {
            var reason = args.Reason == Microsoft.Win32.SessionEndReasons.Logoff
                ? "Abmeldung"
                : "Herunterfahren oder Neustart";
            _monitor?.RecordShutdown(reason);
        };

        // Vor dem Start pruefen, ob die letzte Sitzung sauber endete.
        var previous = MonitorService.CheckPreviousSession(incidents);
        SessionLog.AppendStart(DescribeSystemBriefly(), previous.WasClean, previous.LastHeartbeat);

        _vm = new MainViewModel(_settings, knowledge, incidents, _monitor);
        _window = new MainWindow { DataContext = _vm };

        SetupTray();

        bool autostart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (!(autostart && _settings.StartMinimized))
            _window.Show();

        if (previous.Incident is { } incident)
        {
            _vm.CurrentPage = "monitor";
            _tray?.ShowBalloonTip(8000, AppInfo.Name,
                $"Beim letzten Mal endete die Sitzung unerwartet ({incident.Time:dd.MM. HH:mm}). " +
                "Der Vorfall wurde festgehalten.", ToolTipIcon.Warning);
        }

        if (_settings.AutoStartMonitoring) _monitor.Start();

        _ = InitializeBackgroundAsync(knowledge);
    }

    /// <summary>
    /// Fuehrt einen vollstaendigen Diagnoselauf ohne Oberflaeche aus und beendet
    /// das Programm. Aufruf: PCHelper.exe --selftest
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        int exitCode = 0;
        try
        {
            var knowledge = KnowledgeBase.LoadEmbedded();
            var incidents = new IncidentStore();

            Log.Info("Selbsttest: Diagnose wird ausgefuehrt ...");
            var result = await Diagnostics.CheckEngine.RunAsync(knowledge);

            Log.Info($"Selbsttest: {result.Findings.Count} Befunde in {result.Duration.TotalSeconds:0.#} s " +
                     $"({result.CriticalCount} kritisch, {result.WarningCount} auffaellig, {result.OkCount} in Ordnung).");
            Log.Info("Selbsttest: " + result.Profile.OneLine);

            foreach (var display in result.Profile.Displays)
                Log.Info($"Selbsttest: Anzeige '{display.Name}' -> {display.Connection} " +
                         $"(Rohwert {display.OutputTechnologyRaw}), {display.RefreshHz:0.##} Hz");

            foreach (var finding in result.Findings)
                Log.Info($"Selbsttest:   [{finding.SeverityText}] {finding.Category} / {finding.Title}");

            foreach (var suspicion in result.Suspicions)
                Log.Info($"Selbsttest:   Verdacht {suspicion.Percent:0} % - {suspicion.Title} ({suspicion.Rank})");

            var telemetry = new TelemetryLogger();
            var incidentList = incidents.LoadAll(10);

            var htmlPath = Reporting.ReportBuilder.WriteHtml(result, incidentList, telemetry);
            Log.Info("Selbsttest: HTML-Bericht geschrieben nach " + htmlPath);

            var pdfPath = Reporting.PdfReportBuilder.Write(result, incidentList, telemetry);
            Log.Info("Selbsttest: PDF-Bericht geschrieben nach " + pdfPath);

            if (!CheckSymptomCatalog()) exitCode = 1;
            if (!await CheckUserInterfaceAsync(incidents)) exitCode = 1;
            if (!await CheckMaintenanceAsync()) exitCode = 1;
            if (!await CheckMaintenanceScriptsAsync()) exitCode = 1;
            if (!await CheckGpuSensorsAsync()) exitCode = 1;
            await CheckFocusedRunAsync(knowledge);
        }
        catch (Exception ex)
        {
            Log.Error("Selbsttest fehlgeschlagen", ex);
            exitCode = 1;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    /// <summary>
    /// Ist nvidia-smi vorhanden, muss es Werte liefern. Ein Feldname, den ein neuer Treiber nicht mehr
    /// kennt, laesst sonst die ganze Abfrage scheitern - dann fehlen Live-Werte und gpu-blackbox.csv,
    /// ohne dass irgendetwas auffaellt (so geschehen mit Treiber 616.92).
    /// </summary>
    private static async Task<bool> CheckGpuSensorsAsync()
    {
        if (!Diagnostics.NvidiaSmi.IsAvailable)
        {
            Log.Info("Selbsttest: GPU-Sensorik uebersprungen (kein nvidia-smi).");
            return true;
        }

        var samples = await Diagnostics.NvidiaSmi.SampleAsync();
        if (samples.Count == 0)
        {
            Log.Error("Selbsttest: nvidia-smi ist vorhanden, lieferte aber keine Werte: " + Diagnostics.NvidiaSmi.LastError);
            return false;
        }

        var g = samples[0];
        Log.Info($"Selbsttest: GPU-Sensorik - {g.Name}, Treiber {g.DriverVersion}, {g.TemperatureC:0} C, " +
                 $"{g.PowerWatt:0} W von {g.PowerLimitWatt:0} W, PCIe Gen {g.PcieLinkGenCurrent}/{g.PcieLinkGenMax} " +
                 $"x{g.PcieLinkWidthCurrent}/{g.PcieLinkWidthMax}, Drosselung Leistung={g.ThrottlePowerCap} " +
                 $"Temperatur={g.ThrottleThermal} Hardware={g.ThrottleHwSlowdown}");
        return true;
    }

    /// <summary>
    /// Prueft den Symptomkatalog gegen Reparatur- und Werkzeugliste. Ein Tippfehler
    /// in einer Kennung faellt sonst erst auf, wenn ein Nutzer davorsteht.
    /// </summary>
    private static bool CheckSymptomCatalog()
    {
        var tools = new ViewModels.ToolsViewModel();
        var toolIds = tools.Tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        bool ok = true;

        foreach (var symptom in Diagnostics.SymptomCatalog.All)
        {
            foreach (var fixId in symptom.FixIds)
            {
                if (Fixes.FixCatalog.ById(fixId) is not null) continue;
                Log.Error($"Selbsttest: Symptom '{symptom.Id}' verweist auf unbekannte Reparatur '{fixId}'.");
                ok = false;
            }

            foreach (var toolId in symptom.ToolIds)
            {
                if (toolIds.Contains(toolId)) continue;
                Log.Error($"Selbsttest: Symptom '{symptom.Id}' verweist auf unbekanntes Werkzeug '{toolId}'.");
                ok = false;
            }
        }

        Log.Info($"Selbsttest: {Diagnostics.SymptomCatalog.All.Count} Symptome geprueft - " +
                 (ok ? "alle Verweise gueltig." : "FEHLERHAFTE VERWEISE, siehe oben."));
        return ok;
    }

    /// <summary>
    /// Baut das Hauptfenster samt aller Seiten einmal auf, ohne es anzuzeigen.
    /// Fehlende Ressourcenverweise in XAML fallen sonst erst auf, wenn jemand
    /// die betreffende Seite oeffnet - und dann mit einem Absturz.
    /// </summary>
    private async Task<bool> CheckUserInterfaceAsync(IncidentStore incidents)
    {
        try
        {
            var vm = new MainViewModel(_settings, KnowledgeBase.LoadEmbedded(), incidents,
                new MonitorService(_settings, incidents));

            var window = new MainWindow { DataContext = vm };
            window.Measure(new System.Windows.Size(1180, 780));

            // Die Wartungsseite baut ihre Karten erst auf, wenn Punkte da sind - ein Fehler in der Vorlage
            // (etwa ein fehlender Ressourcenverweis) faellt sonst erst beim ersten Oeffnen auf.
            // Mit PCHELPER_SELFTEST_SHOTS=<Ordner> werden die Seiten zusaetzlich als Bild abgelegt.
            var shots = Environment.GetEnvironmentVariable("PCHELPER_SELFTEST_SHOTS");
            vm.CurrentPage = "maintenance";
            foreach (var section in vm.Maintenance.Sections)
            {
                await vm.Maintenance.SelectAsync(section);
                for (int i = 0; i < 300 && (section.IsBusy || !section.HasScanned); i++) await Task.Delay(100);

                // Das Fenster wird nie angezeigt: Inhalt ausdruecklich vermessen und anordnen.
                var root = (FrameworkElement)window.Content;
                root.Measure(new System.Windows.Size(1180, 780));
                root.Arrange(new Rect(0, 0, 1180, 780));
                root.UpdateLayout();

                Log.Info($"Selbsttest: Wartungsseite '{section.Title}' baut {section.Items.Count} Karten auf.");
                if (!string.IsNullOrEmpty(shots)) SaveScreenshot(root, System.IO.Path.Combine(shots, $"wartung-{section.Id}.png"));
            }

            window.Close();

            Log.Info($"Selbsttest: Oberflaeche laedt fehlerfrei ({vm.Tools.Tools.Count} Werkzeuge, " +
                     $"{vm.Fixes.Items.Count} Reparaturen, {vm.Symptoms.Groups.Count} Symptomgruppen).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Selbsttest: Oberflaeche konnte nicht aufgebaut werden", ex);
            return false;
        }
    }

    private static void SaveScreenshot(FrameworkElement element, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Laesst die drei Wartungsbereiche einmal pruefen. Aktionen werden dabei nie ausgefuehrt -
    /// der Selbsttest darf nichts am System veraendern.
    /// </summary>
    private async Task<bool> CheckMaintenanceAsync()
    {
        var scans = new (string Name, Func<Task<IReadOnlyList<Maintenance.MaintItem>>> Scan)[]
        {
            ("Autostart", () => Maintenance.StartupScanner.ScanAsync(_settings)),
            ("Platz schaffen", () => Maintenance.CleanupScanner.ScanAsync()),
            ("Geraete", () => Maintenance.DeviceItems.ScanAsync()),
        };

        bool ok = true;
        foreach (var (name, scan) in scans)
        {
            try
            {
                var started = DateTime.Now;
                var items = await scan();
                Log.Info($"Selbsttest: Wartung '{name}' - {items.Count} Punkte in {(DateTime.Now - started).TotalSeconds:0.#} s.");

                foreach (var item in items)
                    Log.Info($"Selbsttest:   [{item.Severity}] {item.Group} / {item.Title} = {item.Value}" +
                             (item.HasAction ? $" -> {item.ActionText}" : ""));
            }
            catch (Exception ex)
            {
                Log.Error($"Selbsttest: Wartung '{name}' fehlgeschlagen", ex);
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// Die Wartungsaktionen laufen als PowerShell-Skript mit Administratorrechten und lassen sich im Selbsttest nicht
    /// ausfuehren (UAC). Deshalb wird hier jedes Skript syntaktisch geprueft und der Skriptweg einmal ohne Elevation
    /// mit einem harmlosen Skript durchlaufen.
    /// </summary>
    private static async Task<bool> CheckMaintenanceScriptsAsync()
    {
        var scripts = new (string Name, string Script)[]
        {
            ("Autostart (alle Benutzer)", Maintenance.StartupScanner.ApprovedScript("Run", "Beispiel", enable: false)),
            ("Aufgabe abschalten", Maintenance.StartupScanner.TaskScript("Beispiel's Aufgabe", @"\", enable: false)),
            ("Aufgabe einschalten", Maintenance.StartupScanner.TaskScript("Beispiel", @"\Ordner\", enable: true)),
            ("Dienst auf Manuell", Maintenance.StartupScanner.ServiceScript("Beispiel", automatic: false)),
            ("Dienst auf Automatisch", Maintenance.StartupScanner.ServiceScript("Beispiel", automatic: true)),
            ("Geraet neu einstecken", Maintenance.DeviceItems.RestartScript(@"USB\VID_0000&PID_0002\5&2A1B3C4D&0&1")),
            ("USB-Eintrag entfernen", Maintenance.DeviceItems.RemoveScript(@"USB\VID_0000&PID_0002\5&2A1B3C4D&0&1")),
            ("Stromsparen einzelner Geraete", Maintenance.DeviceItems.PowerSavingScript),
        };

        bool ok = true;
        foreach (var (name, script) in scripts)
        {
            var errors = await PowerShellRunner.CheckSyntaxAsync(script);
            if (errors is null) continue;
            Log.Error($"Selbsttest: Wartungsskript '{name}' hat Syntaxfehler: {errors}");
            ok = false;
        }

        var (success, message) = await PowerShellRunner.RunScriptAsync(
            "Write-Output 'PCH-RESULT:OK|Skriptweg in Ordnung'", "selbsttest", elevated: false);
        if (!success || message != "Skriptweg in Ordnung")
        {
            Log.Error($"Selbsttest: Skriptweg liefert nicht das erwartete Ergebnis (Erfolg={success}, Text='{message}').");
            ok = false;
        }

        Log.Info($"Selbsttest: {scripts.Length} Wartungsskripte geprueft - " + (ok ? "Syntax in Ordnung, Skriptweg funktioniert." : "FEHLER, siehe oben."));
        return ok;
    }

    /// <summary>Fuehrt beispielhaft eine gezielte Untersuchung aus - so wie es die Oberflaeche tut.</summary>
    private static async Task CheckFocusedRunAsync(KnowledgeBase knowledge)
    {
        const string probe = "mein mikrofon wird in discord nicht erkannt";

        var matches = Diagnostics.SymptomMatcher.Match(probe, 3);
        Log.Info($"Selbsttest: Freitext \"{probe}\" ->");
        foreach (var (symptom, score) in matches)
            Log.Info($"Selbsttest:   {score,6:0.#} Punkte - {symptom.Title}");

        var chosen = matches.FirstOrDefault().Symptom ?? Diagnostics.SymptomCatalog.All[0];
        var focused = await Diagnostics.CheckEngine.RunAsync(knowledge, symptom: chosen);

        Log.Info($"Selbsttest: Gezielte Untersuchung '{chosen.Id}' - {focused.ChecksRun} Pruefungen, " +
                 $"{focused.Findings.Count} Befunde in {focused.Duration.TotalSeconds:0.#} s.");

        foreach (var finding in focused.Findings.Take(5))
            Log.Info($"Selbsttest:   Relevanz {finding.RelevanceFor(chosen):0.##} - " +
                     $"[{finding.SeverityText}] {finding.Title}");

        foreach (var suspicion in focused.Suspicions.Take(3))
            Log.Info($"Selbsttest:   Verdacht {suspicion.Percent:0} % - {suspicion.Title}");
    }

    /// <summary>Nicht blockierende Startaufgaben: Aufraeumen, Wissensdatenbank, Update-Suche.</summary>
    private async Task InitializeBackgroundAsync(KnowledgeBase knowledge)
    {
        try
        {
            _monitor?.Telemetry.Cleanup(_settings.TelemetryRetentionDays);

            if (_settings.AutoRefreshKnowledgeBase)
            {
                await knowledge.TryRefreshAsync(_settings.RawIssuesUrl);
                _vm?.RefreshKnowledgeCommand.RaiseCanExecuteChanged();
            }

            if (_settings.AutoCheckUpdates && _vm is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                await _vm.CheckForUpdateAsync(silent: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hintergrundinitialisierung fehlgeschlagen", ex);
        }
    }

    private void SetupTray()
    {
        try
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Fenster oeffnen", null, (_, _) => ShowWindow());
            menu.Items.Add("Vorfall jetzt melden", null, (_, _) =>
            {
                _monitor?.ReportManual("Ueber das Symbol im Infobereich gemeldet.");
                _tray?.ShowBalloonTip(4000, AppInfo.Name,
                    "Der Zeitpunkt wurde festgehalten.", ToolTipIcon.Info);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beenden", null, (_, _) => ExitApplication());

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = $"{AppInfo.Name} {AppInfo.VersionDisplay}",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => ShowWindow();
        }
        catch (Exception ex)
        {
            Log.Error("Symbol im Infobereich konnte nicht angelegt werden", ex);
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Wird vom Hauptfenster beim Schliessen aufgerufen.</summary>
    internal bool HideInsteadOfClose()
    {
        if (!_settings.MinimizeToTray) return false;

        _window?.Hide();
        _tray?.ShowBalloonTip(4000, AppInfo.Name,
            "PC Helper laeuft im Hintergrund weiter und zeichnet Messwerte auf.", ToolTipIcon.Info);
        return true;
    }

    internal void ExitApplication()
    {
        Log.Info("Programm wird beendet.");

        // Wichtig fuers Protokoll: Ab hier entsteht eine Luecke in den Messwerten.
        SessionLog.AppendNote("Programm beendet",
            "Ab hier werden keine Messwerte mehr aufgezeichnet, bis PC Helper wieder laeuft.");

        _monitor?.Stop();
        MonitorService.MarkCleanExit();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        Shutdown();
    }

    /// <summary>
    /// Windows faehrt herunter, startet neu oder meldet ab.
    /// Genau hier wird das Protokoll geschrieben - fuer die Fehlersuche ist der
    /// Zeitpunkt jedes Neustarts eine der wichtigsten Informationen ueberhaupt.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            var reason = e.ReasonSessionEnding == ReasonSessionEnding.Logoff
                ? "Abmeldung"
                : "Herunterfahren oder Neustart";

            Log.Info("Sitzung endet: " + reason);
            _monitor?.RecordShutdown(reason);
        }
        catch (Exception ex)
        {
            Log.Error("Protokoll beim Herunterfahren fehlgeschlagen", ex);
        }

        base.OnSessionEnding(e);
    }

    /// <summary>Kurzbeschreibung des Systems fuer das Sitzungsprotokoll (bewusst guenstig).</summary>
    private static string DescribeSystemBriefly()
    {
        try
        {
            var (_, usedGb, totalGb) = Native.GetMemoryStatus();
            var displays = DisplayConfig.GetActiveTargets();
            var monitors = displays.Count == 0
                ? "keine Anzeige erkannt"
                : string.Join(", ", displays.Select(d => $"{d.Name} ({d.Connection}, {d.RefreshHz:0.##} Hz)"));

            return $"{totalGb:0.#} GB RAM (davon {usedGb:0.#} GB belegt) | Bildschirme: {monitors}";
        }
        catch
        {
            return "Systemdaten nicht ermittelbar";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unbehandelter Fehler in der Oberflaeche", e.Exception);

        // Im kopflosen Selbsttest darf hier keine Dialogbox aufgehen - die wartet
        // sonst ohne Bedienperson auf ewig auf einen Klick und haengt den CI-Lauf auf.
        if (!_isSelfTest)
        {
            MessageBox.Show(
                $"Es ist ein unerwarteter Fehler aufgetreten:\n\n{e.Exception.Message}\n\n" +
                $"Details stehen in der Protokolldatei:\n{AppInfo.LogFile}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
