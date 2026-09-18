using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Monitoring;

/// <summary>Zustand der letzten Sitzung - erkennt harte Ausfaelle.</summary>
internal sealed class SessionState
{
    public DateTime? LastHeartbeat { get; set; }
    public bool CleanExit { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Dauerueberwachung im Hintergrund.
///
/// Der eigentliche Zweck: Ein Schwarzbild hinterlaesst von sich aus keine Spur.
/// Diese Klasse schreibt fortlaufend Messwerte und einen Zeitstempel weg,
/// sodass sich nach einem Vorfall rekonstruieren laesst, was unmittelbar
/// davor passiert ist - und wann genau der Rechner ausgefallen ist.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly TelemetryLogger _logger = new();
    private readonly IncidentStore _incidents;
    private readonly Settings _settings;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _lastSignature;
    private string? _pendingEventMarker;
    private int _shutdownRecorded;
    private int _gpuProcessCycle;

    /// <summary>Alle wie oft Messzyklen die (teurere) Liste der VRAM-Prozesse neu abgefragt wird.</summary>
    private const int GpuProcessSampleEveryNCycles = 5;

    /// <summary>Ab dieser GPU-Auslastung wird jede Sekunde gemessen (ein GPU-Ausfall kommt oft nach wenigen Sekunden Last).</summary>
    private const double GpuBusyPercent = 50;

    // Erkennung einer nicht mehr antwortenden Karte: nvidia-smi liefert Werte, dann ploetzlich nicht mehr.
    private const int GpuFailuresBeforeIncident = 2;
    private bool _gpuEverSampled;
    private int _gpuFailStreak;
    private DateTime _gpuFirstFailure;
    private bool _gpuLostReported;

    public MonitorService(Settings settings, IncidentStore incidents)
    {
        _settings = settings;
        _incidents = incidents;
    }

    /// <summary>Neuer Messpunkt verfuegbar.</summary>
    public event EventHandler<TelemetrySample>? SampleTaken;

    /// <summary>Auffaelligkeit erkannt (z. B. Bildschirm verloren).</summary>
    public event EventHandler<Incident>? IncidentDetected;

    public bool IsRunning => _loop is { IsCompleted: false };

    public TelemetryLogger Telemetry => _logger;

    /// <summary>Der zuletzt erfasste Messpunkt.</summary>
    public TelemetrySample? Latest { get; private set; }

    /// <summary>Die vollstaendige zuletzt gelesene GPU-Momentaufnahme (Fan, Engines, PCIe-Link, Drosselung).</summary>
    public GpuSample? LatestGpu { get; private set; }

    /// <summary>Prozesse, die zuletzt Grafikspeicher belegten (VRAM-genau, seltener aktualisiert als die uebrige Sensorik).</summary>
    public IReadOnlyList<GpuProcessSample> LatestGpuProcesses { get; private set; } = Array.Empty<GpuProcessSample>();

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _lastSignature = DisplayConfig.GetSignature();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _loop = Task.Run(() => RunAsync(_cts.Token));
        Log.Info($"Ueberwachung gestartet (Intervall {_settings.SampleIntervalSeconds} s).");
    }

    public void Stop()
    {
        if (_cts is null) return;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* Abbruch ist erwartet */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;

        Log.Info("Ueberwachung gestoppt.");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Erster Aufruf setzt nur den Vergleichswert fuer die CPU-Auslastung.
        Native.GetCpuUsagePercent();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sample = await CollectAsync(ct);
                Latest = sample;
                _logger.Append(sample);
                WriteHeartbeat();
                SampleTaken?.Invoke(this, sample);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("Messpunkt fehlgeschlagen: " + ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(NextDelaySeconds()), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Normalerweise das eingestellte Intervall. Unter hoher GPU-Last oder bei einer nicht mehr
    /// antwortenden Karte jede Sekunde - genau dann entscheidet sich, ob nach einem Ausfall
    /// noch aussagekraeftige Werte auf der Platte liegen.
    /// </summary>
    private int NextDelaySeconds()
    {
        var configured = Math.Clamp(_settings.SampleIntervalSeconds, 2, 120);
        bool busy = Latest?.GpuUtilPercent is >= GpuBusyPercent;
        return busy || _gpuFailStreak > 0 ? 1 : configured;
    }

    private async Task<TelemetrySample> CollectAsync(CancellationToken ct)
    {
        var (ramPercent, ramUsed, _) = Native.GetMemoryStatus();
        var displays = DisplayConfig.GetActiveTargets();
        var signature = DisplayConfig.GetSignature();

        var sample = new TelemetrySample
        {
            Time = DateTime.Now,
            CpuPercent = Round(Native.GetCpuUsagePercent()),
            RamPercent = Round(ramPercent),
            RamUsedGb = Round(ramUsed),
            DisplayCount = displays.Count,
            DisplaySignature = signature,
            UptimeSeconds = (long)Native.GetUptime().TotalSeconds,
            Event = Interlocked.Exchange(ref _pendingEventMarker, null),
        };

        var gpus = await NvidiaSmi.SampleAsync(ct);
        if (gpus.Count > 0)
        {
            var g = gpus[0];
            sample.GpuTempC = g.TemperatureC;
            sample.GpuUtilPercent = g.UtilizationPercent;
            sample.GpuPowerWatt = g.PowerWatt;
            sample.GpuClockMhz = g.ClockMhz;
            sample.GpuMemoryMb = g.MemoryUsedMb;
            sample.GpuState = g.PerformanceState;
            LatestGpu = g;

            GpuBlackbox.Append(sample.Time, g);
            HandleGpuRecovered(sample);
            _gpuEverSampled = true;

            // Die Liste der VRAM-Prozesse ist ein eigener nvidia-smi-Aufruf und aendert
            // sich langsamer als die Sensorik - seltener abfragen spart Prozessstarts.
            if (_gpuProcessCycle++ % GpuProcessSampleEveryNCycles == 0)
                LatestGpuProcesses = await NvidiaSmi.SampleProcessesAsync(ct);
        }
        else
        {
            LatestGpu = null;
            HandleGpuFailure(sample);
        }

        DetectDisplayChange(sample, signature, displays.Count);
        return sample;
    }

    /// <summary>
    /// nvidia-smi lieferte zuvor Werte und jetzt nicht mehr: Die Karte antwortet nicht mehr,
    /// waehrend Windows und diese Anwendung weiterlaufen. Das ist der GPU-Haenger.
    /// Ein einzelner Fehlversuch zaehlt nicht (nvidia-smi kann unter Last auch einmal zu spaet antworten).
    /// </summary>
    private void HandleGpuFailure(TelemetrySample sample)
    {
        // Ohne vorherigen Erfolg (kein NVIDIA-Treiber, kein nvidia-smi) gibt es nichts zu melden.
        if (!_gpuEverSampled || !NvidiaSmi.IsAvailable) return;

        if (_gpuFailStreak++ == 0) _gpuFirstFailure = sample.Time;
        if (_gpuFailStreak < GpuFailuresBeforeIncident || _gpuLostReported) return;

        _gpuLostReported = true;
        var error = NvidiaSmi.LastError;
        sample.Event = "Grafikkarte antwortet nicht mehr";

        GpuBlackbox.AppendNote(_gpuFirstFailure, GpuBlackbox.LostMarker, error);

        var lastGpu = GpuBlackbox.Describe(_gpuFirstFailure, 6);
        var note =
            "Die Grafikkarte antwortet nicht mehr auf Abfragen, waehrend Windows und diese Anwendung weiterlaufen. " +
            "Das ist ein Haenger der GPU selbst (Bild schwarz, Ton und Netzwerk laufen weiter) - kein Ausfall des ganzen Rechners.\n\n" +
            $"Erster fehlgeschlagener Abruf: {_gpuFirstFailure:dd.MM.yyyy HH:mm:ss}\n" +
            $"Meldung von nvidia-smi: {(string.IsNullOrWhiteSpace(error) ? "-" : Trim(error, 400))}" +
            (lastGpu is null ? "" : "\n\n" + lastGpu);

        SessionLog.AppendNote("Grafikkarte antwortet nicht mehr", note);

        var incident = _incidents.Add(IncidentKind.GpuUnresponsive, "Grafikkarte antwortet nicht mehr", note, time: _gpuFirstFailure);
        IncidentDetected?.Invoke(this, incident);
    }

    private void HandleGpuRecovered(TelemetrySample sample)
    {
        if (_gpuFailStreak == 0) return;

        if (_gpuLostReported)
        {
            GpuBlackbox.AppendNote(sample.Time, GpuBlackbox.BackMarker);
            SessionLog.AppendNote("Grafikkarte antwortet wieder",
                $"Ausfall seit {_gpuFirstFailure:HH:mm:ss}, Dauer {(sample.Time - _gpuFirstFailure).TotalSeconds:0} s.");
            sample.Event = "Grafikkarte antwortet wieder";
        }

        _gpuFailStreak = 0;
        _gpuLostReported = false;
    }

    private static string Trim(string s, int max)
    {
        var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > max ? flat[..max] + " ..." : flat;
    }

    /// <summary>
    /// Kern der automatischen Erkennung: Aendert sich die Anzeigekonfiguration,
    /// ohne dass jemand etwas umgesteckt hat, ist genau das der Signalverlust.
    /// </summary>
    private void DetectDisplayChange(TelemetrySample sample, string signature, int displayCount)
    {
        if (_lastSignature is null)
        {
            _lastSignature = signature;
            return;
        }

        if (string.Equals(_lastSignature, signature, StringComparison.Ordinal)) return;

        var before = _lastSignature;
        _lastSignature = signature;

        var lost = displayCount == 0 || signature == "keine";
        var title = lost
            ? "Alle Bildschirme haben das Signal verloren"
            : "Die Anzeigekonfiguration hat sich veraendert";

        sample.Event = title;

        var incident = _incidents.Add(
            IncidentKind.DisplayChange,
            title,
            note: "Automatisch erkannt: Die aktive Anzeigekonfiguration hat gewechselt. " +
                  "Wenn zu diesem Zeitpunkt niemand ein Kabel umgesteckt oder die Aufloesung geaendert hat, " +
                  "ist das der gesuchte Signalabriss.",
            before: before,
            after: signature);

        IncidentDetected?.Invoke(this, incident);
    }

    /// <summary>
    /// Haelt fest, dass Windows heruntergefahren oder neu gestartet wird.
    /// Wird aus dem SessionEnding-Ereignis aufgerufen und muss zuegig sein.
    /// </summary>
    public Incident? RecordShutdown(string reasonText)
    {
        // Windows kann das Sitzungsende ueber mehrere Wege melden - nur einmal schreiben.
        if (Interlocked.Exchange(ref _shutdownRecorded, 1) == 1) return null;

        SessionLog.AppendShutdown(reasonText, Latest, _lastSignature);

        var incident = _incidents.Add(
            IncidentKind.SystemShutdown,
            reasonText,
            note: $"Laufzeit dieser Sitzung: {FormatUptime(Native.GetUptime())}.\n\n" +
                  "Wenn dieser Neustart noetig war, um ein Schwarzbild loszuwerden, laesst er sich " +
                  "beim naechsten Start als Vorfall bestaetigen. Die Messwerte davor sind gesichert.");

        MarkCleanExit();
        return incident;
    }

    /// <summary>
    /// Bestaetigt nachtraeglich, dass ein Neustart wegen eines Schwarzbildes noetig war.
    /// </summary>
    public Incident ConfirmBlackscreenRestart(DateTime when, string? note)
    {
        SessionLog.AppendNote("Neustart wegen Schwarzbild bestaetigt",
            $"Betroffener Neustart: {when:dd.MM.yyyy HH:mm:ss}\n{note}");

        var incident = _incidents.Add(
            IncidentKind.BlackscreenRestart,
            "Neustart war wegen eines Schwarzbildes noetig",
            note: note ?? "Vom Nutzer beim naechsten Start bestaetigt.",
            time: when);

        IncidentDetected?.Invoke(this, incident);
        return incident;
    }

    /// <summary>Die zuletzt beobachtete Signatur der Anzeigekonfiguration.</summary>
    public string? CurrentDisplaySignature => _lastSignature;

    internal static string FormatUptime(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} Std {t.Minutes} Min" : $"{t.Minutes} Min";

    /// <summary>Meldet einen Vorfall, den der Nutzer selbst beobachtet hat.</summary>
    public Incident ReportManual(string note)
    {
        _pendingEventMarker = "Vom Nutzer gemeldet";
        var incident = _incidents.Add(IncidentKind.Manual, "Vom Nutzer gemeldeter Vorfall", note);
        IncidentDetected?.Invoke(this, incident);
        return incident;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _pendingEventMarker = e.Mode switch
        {
            PowerModes.Resume => "Aufwachen aus dem Energiesparmodus",
            PowerModes.Suspend => "Wechsel in den Energiesparmodus",
            _ => "Aenderung des Energiestatus",
        };
        Log.Info("Energiestatus: " + _pendingEventMarker);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _pendingEventMarker = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "Bildschirm gesperrt",
            SessionSwitchReason.SessionUnlock => "Bildschirm entsperrt",
            _ => "Sitzungswechsel: " + e.Reason,
        };
    }

    // ---------- Sitzungszustand: Erkennung harter Ausfaelle ----------

    private static string SessionFile => Path.Combine(AppInfo.DataDir, "session.json");

    private void WriteHeartbeat()
    {
        try
        {
            var state = new SessionState { LastHeartbeat = DateTime.Now, CleanExit = false, StartedAt = _startedAt };
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(state));
        }
        catch { /* Herzschlag ist Beiwerk - Fehler hier duerfen nichts stoppen */ }
    }

    private static DateTime? _startedAt;

    /// <summary>Ergebnis der Auswertung der vorherigen Sitzung.</summary>
    public sealed record PreviousSession(bool WasClean, DateTime? LastHeartbeat, Incident? Incident);

    /// <summary>
    /// Prueft beim Programmstart, ob die vorherige Sitzung sauber endete.
    /// Ist das nicht der Fall, wird ein Vorfall mit dem letzten Lebenszeichen
    /// angelegt - das ist der genaue Zeitpunkt, an dem der Rechner ausfiel.
    /// </summary>
    public static PreviousSession CheckPreviousSession(IncidentStore store)
    {
        Incident? result = null;
        bool wasClean = true;
        DateTime? heartbeat = null;

        try
        {
            if (File.Exists(SessionFile))
            {
                var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionFile));
                heartbeat = state?.LastHeartbeat;
                wasClean = state?.CleanExit ?? true;

                if (state is { CleanExit: false, LastHeartbeat: not null })
                {
                    var last = state.LastHeartbeat.Value;
                    var bootTime = DateTime.Now - Native.GetUptime();
                    bool rebooted = bootTime > last;

                    // Kurze Luecken (z. B. Abmelden) nicht als Ausfall werten.
                    if ((DateTime.Now - last).TotalMinutes >= 1)
                    {
                        // Die Blackbox der GPU liegt auch nach hartem Ausfall auf der Platte.
                        var gpuTail = rebooted ? GpuBlackbox.Describe(last, 6) : null;
                        if (gpuTail is not null) SessionLog.AppendNote("GPU-Werte vor dem unsauberen Sitzungsende", gpuTail);

                        result = store.Add(
                            IncidentKind.UncleanShutdown,
                            rebooted
                                ? "Rechner wurde nicht ordentlich heruntergefahren"
                                : "Programm wurde unerwartet beendet",
                            note: rebooted
                                ? $"Letztes Lebenszeichen der Ueberwachung: {last:dd.MM.yyyy HH:mm:ss}.\n" +
                                  $"Windows startete danach um {bootTime:dd.MM.yyyy HH:mm:ss} neu.\n\n" +
                                  "Das ist der Zeitpunkt, an dem der Rechner ausgefallen ist. Die Messwerte " +
                                  "unmittelbar davor stehen im Bericht zu diesem Vorfall." +
                                  (gpuTail is null ? "" : "\n\n" + gpuTail)
                                : $"Letztes Lebenszeichen: {last:dd.MM.yyyy HH:mm:ss}. " +
                                  "Windows lief durch - vermutlich wurde nur das Programm beendet.",
                            time: last);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Sitzungszustand nicht auswertbar: " + ex.Message);
        }

        _startedAt = DateTime.Now;
        try
        {
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(
                new SessionState { LastHeartbeat = DateTime.Now, CleanExit = false, StartedAt = _startedAt }));
        }
        catch { /* nicht kritisch */ }

        return new PreviousSession(wasClean, heartbeat, result);
    }

    /// <summary>Beim regulaeren Beenden aufrufen, damit kein Fehlalarm entsteht.</summary>
    public static void MarkCleanExit()
    {
        try
        {
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(
                new SessionState { LastHeartbeat = DateTime.Now, CleanExit = true, StartedAt = _startedAt }));
        }
        catch { /* nicht kritisch */ }
    }

    private static double? Round(double v) => double.IsNaN(v) ? null : Math.Round(v, 1);

    public void Dispose() => Stop();
}
