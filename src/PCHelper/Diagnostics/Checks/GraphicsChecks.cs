using System.Text;

namespace PCHelper.Diagnostics.Checks;

/// <summary>
/// Bewertet die Signalstrecke zum Monitor. Beim Muster "Bild weg, Ton laeuft weiter"
/// ist genau das der wahrscheinlichste Ort des Problems.
/// </summary>
public sealed class DisplayConnectionCheck : ICheck
{
    public string Name => "Monitoranbindung";
    public string Category => "Anzeige";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var displays = ctx.Profile.Displays;
        var findings = new List<Finding>();

        if (displays.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "display-none", Category = Category, Title = "Keine Anzeigeinformationen",
                Severity = Severity.Info,
                Summary = "Die aktive Anzeigekonfiguration konnte nicht ausgelesen werden.",
            });
            return Task.FromResult<IEnumerable<Finding>>(findings);
        }

        var sb = new StringBuilder();
        foreach (var d in displays)
            sb.AppendLine($"{d.Name}\n    Anschluss:  {d.Connection} (Kennung {d.OutputTechnologyRaw})\n" +
                          $"    Bildrate:   {d.RefreshHz:0.##} Hz");

        var highRefresh = displays.Where(d => d.IsExternalDigital && d.RefreshHz >= 120).ToList();
        bool anyDisplayPort = displays.Any(d => d.IsDisplayPort);

        // Kernbefund: hohe Bildrate ueber eine externe digitale Strecke ist die
        // klassische Konstellation fuer sporadischen Signalabriss (Link-Training / DSC).
        if (highRefresh.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "display-dp-highrate",
                Category = Category,
                Title = "Hohe Bildwiederholrate an der Monitorverbindung",
                Severity = Severity.Warning,
                Summary = $"{highRefresh.Count} Bildschirm(e) laufen mit " +
                          $"{string.Join(" / ", highRefresh.Select(d => $"{d.RefreshHz:0.##} Hz"))} " +
                          $"ueber {string.Join(" bzw. ", highRefresh.Select(d => d.Connection).Distinct())}.",
                Detail = sb.ToString().TrimEnd() +
                         "\n\nBei hohen Bildraten arbeitet die Verbindung nahe an der Bandbreitengrenze und nutzt " +
                         "Komprimierung (DSC). Minderwertige oder zu lange Kabel, Adapter und Verteiler fuehren dann " +
                         "zu sporadischem Signalverlust: Das Bild ist schlagartig weg, der Rechner laeuft aber weiter - " +
                         "der Ton bleibt hoerbar." +
                         (anyDisplayPort
                             ? "\n\nDisplayPort ist hier besonders relevant: Der Link wird staendig neu ausgehandelt, " +
                               "etwa nach dem Abschalten des Bildschirms oder beim Aufwachen."
                             : "\n\nHinweis: Manche Grafiktreiber melden eine HDMI-Verbindung als 'DVI'. " +
                               "Der tatsaechlich genutzte Anschluss laesst sich nur am Geraet ablesen."),
                Recommendation =
                    "Gezielter Test in dieser Reihenfolge:\n" +
                    "1) Einen Bildschirm testweise auf 60 Hz stellen und beobachten, ob das Schwarzbild ausbleibt.\n" +
                    "2) Kabel tauschen (zertifiziert, moeglichst kurz, keine Adapter/Verteiler).\n" +
                    "3) Testweise nur einen Bildschirm anschliessen - so laesst sich eingrenzen, welcher Ausgang betroffen ist.\n" +
                    "4) Denselben Bildschirm testweise ueber einen anderen Anschluss betreiben (HDMI statt DisplayPort).",
                Causes = new Dictionary<Cause, double> { [Cause.DisplayLink] = 0.7, [Cause.GpuDriver] = 0.2 },
            });
        }
        else
        {
            findings.Add(new Finding
            {
                Id = "display-dp-highrate", Category = Category, Title = "Anzeigekonfiguration unauffaellig",
                Severity = Severity.Ok,
                Summary = $"{displays.Count} aktive(r) Bildschirm(e), keine kritisch hohe Bildwiederholrate.",
                Detail = sb.ToString().TrimEnd(),
            });
        }

        if (displays.Count > 1)
        {
            findings.Add(new Finding
            {
                Id = "display-multi",
                Category = Category,
                Title = "Mehrere Bildschirme aktiv",
                Severity = Severity.Info,
                Summary = $"Es sind {displays.Count} Bildschirme angeschlossen ({string.Join(", ", displays.Select(d => d.Connection))}).",
                Detail = "Mehrmonitor-Betrieb erhoeht die Wahrscheinlichkeit von Anzeigeproblemen: Die Grafikkarte muss " +
                         "mehrere unabhaengige Signalstrecken stabil halten und wechselt haeufiger den Speichertakt.\n\n" +
                         sb.ToString().TrimEnd(),
                Recommendation = "Zum Eingrenzen fuer ein bis zwei Tage nur einen Bildschirm betreiben. " +
                                 "Bleibt das Schwarzbild aus, liegt es an der zweiten Signalstrecke oder am Zusammenspiel beider.",
                Causes = new Dictionary<Cause, double> { [Cause.DisplayLink] = 0.25 },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Sucht nach Grafiktreiber-Resets (TDR) und Treiberabstuerzen im Ereignisprotokoll.</summary>
public sealed class DisplayDriverCrashCheck : ICheck
{
    public string Name => "Grafiktreiber-Abstuerze";
    public string Category => "Anzeige";

    private static readonly string[] Providers =
    {
        "Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfxn", "Microsoft-Windows-DxgKrnl",
    };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var events = EventLogService.Query("System",
            EventLogService.XpathProviders(Providers, ctx.LookbackDays), 120);

        // 4101 = "Der Anzeigetreiber reagiert nicht mehr und wurde wiederhergestellt".
        var tdr = events.Where(e => e.Id == 4101).ToList();
        var others = events.Where(e => e.Id != 4101 && e.Level is "Fehler" or "Kritisch").ToList();

        if (events.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "gpu-tdr", Category = Category, Title = "Keine Grafiktreiber-Abstuerze protokolliert",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen hat Windows keinen Grafiktreiber-Reset aufgezeichnet.",
                    Detail = "Wichtig: Ein Signalabriss auf der Kabelstrecke hinterlaesst hier KEINEN Eintrag. " +
                             "Dieser Befund schliesst ein Kabel-/DisplayPort-Problem also nicht aus - " +
                             "er entlastet nur den Grafiktreiber.",
                }
            });
        }

        var findings = new List<Finding>();
        var sb = new StringBuilder();

        if (tdr.Count > 0)
        {
            sb.AppendLine($"{tdr.Count} Treiber-Resets (Ereignis-ID 4101):");
            foreach (var e in tdr.Take(20)) sb.AppendLine("  " + e);
            sb.AppendLine();
        }
        if (others.Count > 0)
        {
            sb.AppendLine($"{others.Count} weitere Grafik-Fehlerereignisse:");
            foreach (var e in others.Take(20)) sb.AppendLine("  " + e);
        }

        findings.Add(new Finding
        {
            Id = "gpu-tdr",
            Category = Category,
            Title = tdr.Count > 0 ? "Grafiktreiber hat sich zurueckgesetzt (TDR)" : "Fehlerereignisse des Grafiktreibers",
            Severity = tdr.Count > 0 ? Severity.Critical : Severity.Warning,
            Summary = tdr.Count > 0
                ? $"Windows hat {tdr.Count}x einen haengenden Grafiktreiber zurueckgesetzt - zuletzt am {tdr.Max(e => e.Time):dd.MM.yyyy HH:mm}."
                : $"{others.Count} Fehlerereignisse rund um den Grafiktreiber in den letzten {ctx.LookbackDays} Tagen.",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = events.Count,
            LastOccurrence = events.Max(e => e.Time),
            Recommendation =
                "Das ist der wichtigste Einzelbefund fuer 'Bild weg, Ton laeuft weiter'.\n\n" +
                "1) Grafiktreiber sauber neu installieren: alten Treiber mit DDU im abgesicherten Modus entfernen, " +
                "danach den aktuellen Treiber frisch installieren (keine Aktualisierung darueber).\n" +
                "2) Jegliche Uebertaktung der Grafikkarte zuruecknehmen (auch Untervolting-Kurven).\n" +
                "3) Overlays und Tuning-Software (Afterburner/RivaTuner, iCUE, Armoury Crate) testweise beenden.",
            FixIds = new[] { "tdr-delay" },
            Causes = new Dictionary<Cause, double> { [Cause.GpuDriver] = 0.85, [Cause.Software] = 0.2 },
            Links = new[]
            {
                new FindingLink { Label = "NVIDIA-Treiber herunterladen", Url = "https://www.nvidia.com/de-de/geforce/drivers/" },
                new FindingLink { Label = "Display Driver Uninstaller (DDU)", Url = "https://www.wagnardsoft.com/display-driver-uninstaller-DDU-" },
            },
        });

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Bewertet Version und Alter des Grafiktreibers.</summary>
public sealed class GpuDriverCheck : ICheck
{
    public string Name => "Grafiktreiber-Version";
    public string Category => "Anzeige";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        foreach (var gpu in ctx.Profile.Gpus)
        {
            // Virtuelle Anzeigegeraete (Remote-Sitzungen, Capture-Tools) ausblenden.
            if (gpu.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                gpu.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;

            var age = gpu.DriverAgeDays;
            var detail =
                $"Grafikkarte:  {gpu.Name}\n" +
                $"Treiber:      {gpu.DriverDisplay}\n" +
                $"Treiberdatum: {(gpu.DriverDate is null ? "unbekannt" : gpu.DriverDate.Value.ToString("d"))}" +
                (age is null ? "" : $" ({age} Tage alt)");

            Severity severity;
            string title, summary, recommendation;

            if (age is null)
            {
                severity = Severity.Info;
                title = $"Grafiktreiber: {gpu.Name}";
                summary = $"Treiberversion {gpu.DriverDisplay}, Alter nicht ermittelbar.";
                recommendation = "Treiberversion mit der aktuellen Version auf der Herstellerseite vergleichen.";
            }
            else if (age > 270)
            {
                severity = Severity.Warning;
                title = "Grafiktreiber ist deutlich veraltet";
                summary = $"Der Treiber ({gpu.DriverDisplay}) ist {age} Tage alt.";
                recommendation = "Aktuellen Treiber installieren - am besten als Neuinstallation nach DDU-Bereinigung.";
            }
            else if (age <= 21)
            {
                severity = Severity.Info;
                title = "Grafiktreiber wurde vor Kurzem aktualisiert";
                summary = $"Der Treiber ({gpu.DriverDisplay}) wurde vor {age} Tagen installiert.";
                recommendation =
                    "Wichtige Frage zur Eingrenzung: Traten die Schwarzbilder VOR oder ERST NACH diesem Treiberwechsel auf?\n" +
                    "Erst danach: Testweise auf die vorherige Treiberversion zurueckgehen (NVIDIA bietet aeltere Versionen im Archiv an). " +
                    "Bleibt das Problem damit aus, ist der Treiber die Ursache.";
            }
            else
            {
                severity = Severity.Ok;
                title = "Grafiktreiber ist aktuell";
                summary = $"Treiberversion {gpu.DriverDisplay}, {age} Tage alt.";
                recommendation = "";
            }

            findings.Add(new Finding
            {
                Id = "gpu-driver-" + gpu.Name.GetHashCode().ToString("x"),
                Category = Category,
                Title = title,
                Severity = severity,
                Summary = summary,
                Detail = detail,
                Recommendation = string.IsNullOrEmpty(recommendation) ? null : recommendation,
                Causes = severity == Severity.Ok
                    ? new Dictionary<Cause, double>()
                    : new Dictionary<Cause, double> { [Cause.GpuDriver] = 0.5 },
                Links = gpu.IsNvidia
                    ? new[]
                    {
                        new FindingLink { Label = "NVIDIA-Treiber", Url = "https://www.nvidia.com/de-de/geforce/drivers/" },
                        new FindingLink { Label = "NVIDIA Treiberarchiv", Url = "https://www.nvidia.com/de-de/drivers/results/" },
                    }
                    : Array.Empty<FindingLink>(),
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Prueft die TDR-Einstellungen (Timeout Detection and Recovery) in der Registry.</summary>
public sealed class TdrSettingsCheck : ICheck
{
    public string Name => "TDR-Einstellungen";
    public string Category => "Anzeige";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        var delay = p.TdrDelaySeconds ?? 2;      // Windows-Standard: 2 Sekunden
        var level = p.TdrLevel ?? 3;             // Windows-Standard: 3 (Recover on timeout)

        var detail =
            $"TdrDelay: {(p.TdrDelaySeconds is null ? "nicht gesetzt (Standard 2 s)" : delay + " s")}\n" +
            $"TdrLevel: {(p.TdrLevel is null ? "nicht gesetzt (Standard 3 = Wiederherstellung aktiv)" : level.ToString())}\n\n" +
            "Registrierungspfad: HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";

        Finding f;
        if (level == 0)
        {
            f = new Finding
            {
                Id = "tdr-settings", Category = Category, Title = "TDR-Wiederherstellung ist abgeschaltet",
                Severity = Severity.Warning,
                Summary = "TdrLevel steht auf 0. Windows kann einen haengenden Grafiktreiber dann nicht mehr zurueckholen.",
                Detail = detail,
                Recommendation = "Diese Einstellung stammt meist von einem Tuning-Guide oder Entwicklerwerkzeug. " +
                                 "Ohne TDR fuehrt jeder Treiberhaenger zum dauerhaften Schwarzbild statt zu einer kurzen Unterbrechung. " +
                                 "Standardverhalten wiederherstellen.",
                FixIds = new[] { "tdr-reset" },
                Causes = new Dictionary<Cause, double> { [Cause.GpuDriver] = 0.5 },
            };
        }
        else
        {
            f = new Finding
            {
                Id = "tdr-settings", Category = Category, Title = "TDR-Einstellungen im Normalbereich",
                Severity = Severity.Ok,
                Summary = $"Wiederherstellung aktiv, Zeitlimit {delay} Sekunden.",
                Detail = detail,
                Recommendation = "Falls das Bild kurz schwarz wird und von selbst zurueckkommt, kann ein hoeheres Zeitlimit " +
                                 "(10 s) helfen, den Treiber-Reset zu vermeiden. Das ist ein Symptom-Workaround, keine Ursachenbehebung.",
                FixIds = new[] { "tdr-delay" },
            };
        }

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Momentaufnahme der GPU-Sensorik (Temperatur, Leistungsaufnahme).</summary>
public sealed class LiveSensorCheck : ICheck
{
    public string Name => "GPU-Sensoren";
    public string Category => "Temperatur";

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var samples = await NvidiaSmi.SampleAsync(ct);
        if (samples.Count == 0)
        {
            return new[]
            {
                new Finding
                {
                    Id = "gpu-sensors", Category = Category, Title = "Keine GPU-Sensordaten",
                    Severity = Severity.Info,
                    Summary = NvidiaSmi.IsAvailable
                        ? "nvidia-smi lieferte keine Werte."
                        : "nvidia-smi wurde nicht gefunden (nur bei NVIDIA-Grafikkarten verfuegbar).",
                    Detail = "Temperaturen lassen sich alternativ mit HWiNFO64 auslesen. " +
                             "Die Dauerueberwachung dieser App zeichnet die Werte automatisch mit, sobald sie verfuegbar sind.",
                }
            };
        }

        var findings = new List<Finding>();
        foreach (var s in samples)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Grafikkarte:      {s.Name}");
            sb.AppendLine($"Treiber:          {s.DriverVersion}");
            sb.AppendLine($"Temperatur:       {Fmt(s.TemperatureC, "°C")}");
            sb.AppendLine($"Auslastung:       {Fmt(s.UtilizationPercent, "%")}");
            sb.AppendLine($"Leistung:         {Fmt(s.PowerWatt, "W")} von {Fmt(s.PowerLimitWatt, "W")}");
            sb.AppendLine($"Takt:             {Fmt(s.ClockMhz, "MHz")}");
            sb.AppendLine($"Speicher belegt:  {Fmt(s.MemoryUsedMb, "MB")} von {Fmt(s.MemoryTotalMb, "MB")}");
            sb.AppendLine($"Leistungszustand: {s.PerformanceState ?? "-"}");

            var hot = s.TemperatureC is > 83;
            findings.Add(new Finding
            {
                Id = "gpu-sensors",
                Category = Category,
                Title = hot ? "GPU-Temperatur bereits im Leerlauf hoch" : "GPU-Sensorwerte im Normalbereich",
                Severity = hot ? Severity.Warning : Severity.Ok,
                Summary = $"{s.Name}: {Fmt(s.TemperatureC, "°C")} bei {Fmt(s.UtilizationPercent, "%")} Auslastung.",
                Detail = sb.ToString().TrimEnd(),
                Recommendation = hot
                    ? "Gehaeusebelueftung und Staub pruefen. Anhaltend hohe Temperaturen fuehren zu Drosselung und " +
                      "im Extremfall zu Schutzabschaltungen."
                    : "Aussagekraeftig wird die Temperatur erst unter Last. Die Dauerueberwachung dieser App zeichnet " +
                      "den Verlauf auf - damit laesst sich pruefen, ob es kurz vor einem Schwarzbild heiss wurde.",
                Causes = hot ? new Dictionary<Cause, double> { [Cause.Thermal] = 0.6 } : new Dictionary<Cause, double>(),
            });
        }

        return findings;
    }

    private static string Fmt(double? v, string unit) => v is null ? "-" : $"{v.Value:0.#} {unit}";
}
