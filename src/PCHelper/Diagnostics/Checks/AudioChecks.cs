using System.Text;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Formuliert einen Datenschutz-Befund - gleiche Logik fuer Mikrofon und Kamera.</summary>
internal static class ConsentReport
{
    /// <summary>
    /// Baut aus den Zustimmungseinstellungen einen Befund. <paramref name="symptomIds"/>
    /// sorgt dafuer, dass der Befund bei der passenden Frage ganz oben steht.
    /// </summary>
    public static Finding Build(
        PrivacyConsent? consent, string category, string idPrefix, string settingsHint,
        Cause deviceCause, string[] symptomIds, string[] fixIds)
    {
        if (consent is null)
        {
            return new Finding
            {
                Id = idPrefix + "-consent",
                Category = category,
                Title = "Zugriffsrechte nicht auslesbar",
                Severity = Severity.Info,
                Summary = "Die Datenschutzeinstellungen konnten nicht gelesen werden.",
                Recommendation = settingsHint,
                SymptomIds = symptomIds,
            };
        }

        var detail = new StringBuilder();
        detail.AppendLine($"Zugriff fuer Apps insgesamt:      {Describe(consent.UserValue)}");
        detail.AppendLine($"Zugriff fuer Desktop-Programme:   {Describe(consent.DesktopAppsValue)}");
        detail.AppendLine($"Vorgabe per Gruppenrichtlinie:    {Describe(consent.PolicyValue)}");
        if (consent.DeniedApps.Count > 0)
        {
            detail.AppendLine();
            detail.AppendLine("Ausdruecklich gesperrte Anwendungen:");
            foreach (var app in consent.DeniedApps.Take(25)) detail.AppendLine("  - " + app);
        }

        // Der wichtigste Fall zuerst: Desktop-Programme gesperrt. Genau dann sieht
        // das Geraet in Windows einwandfrei aus und die Anwendung findet es trotzdem nicht.
        if (consent.PolicyBlocked)
        {
            return new Finding
            {
                Id = idPrefix + "-consent",
                Category = category,
                Title = $"{consent.Kind}: Zugriff per Richtlinie gesperrt",
                Severity = Severity.Critical,
                Summary = "Eine Gruppenrichtlinie verbietet den Zugriff systemweit - die Einstellungen-App kann das nicht ueberstimmen.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Diese Sperre stammt aus einer Richtlinie (haeufig bei Firmen- oder Schulgeraeten, " +
                                 "manchmal auch aus einem 'Privacy-Tool'). Sie muss dort aufgehoben werden, wo sie " +
                                 "gesetzt wurde, sonst kommt sie nach jedem Neustart zurueck.",
                Causes = new Dictionary<Cause, double> { [Cause.AppPermission] = 0.9 },
                SymptomIds = symptomIds,
                FixIds = fixIds,
            };
        }

        if (consent.GloballyBlocked || consent.DesktopAppsBlocked)
        {
            var what = consent.GloballyBlocked && consent.DesktopAppsBlocked
                ? "weder Apps noch klassische Desktop-Programme"
                : consent.GloballyBlocked ? "keine App" : "keine klassischen Desktop-Programme";

            return new Finding
            {
                Id = idPrefix + "-consent",
                Category = category,
                Title = $"{consent.Kind}: Windows blockiert den Zugriff",
                Severity = Severity.Critical,
                Summary = $"Laut Datenschutzeinstellungen duerfen {what} auf {consent.Kind} zugreifen.",
                Detail = detail.ToString().TrimEnd() +
                         "\n\nDas Geraet selbst ist damit voellig in Ordnung - es wird nur nicht durchgereicht. " +
                         "Die Anwendung meldet in diesem Fall meistens 'kein Geraet gefunden' statt 'Zugriff verweigert', " +
                         "was die Suche regelmaessig in die falsche Richtung schickt.",
                Recommendation = settingsHint,
                Causes = new Dictionary<Cause, double> { [Cause.AppPermission] = 1.0, [deviceCause] = 0.2 },
                SymptomIds = symptomIds,
                FixIds = fixIds,
            };
        }

        if (consent.DeniedApps.Count > 0)
        {
            return new Finding
            {
                Id = idPrefix + "-consent",
                Category = category,
                Title = $"{consent.Kind}: einzelne Anwendungen sind gesperrt",
                Severity = Severity.Warning,
                Summary = $"{consent.DeniedApps.Count} Anwendung(en) duerfen nicht auf {consent.Kind} zugreifen: " +
                          string.Join(", ", consent.DeniedApps.Take(4)) +
                          (consent.DeniedApps.Count > 4 ? " ..." : ""),
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Wenn die betroffene Anwendung darunter ist, den Schalter in den Datenschutzeinstellungen " +
                                 "wieder umlegen. " + settingsHint,
                Causes = new Dictionary<Cause, double> { [Cause.AppPermission] = 0.6 },
                SymptomIds = symptomIds,
                FixIds = fixIds,
            };
        }

        return new Finding
        {
            Id = idPrefix + "-consent",
            Category = category,
            Title = $"{consent.Kind}: Zugriff ist erlaubt",
            Severity = Severity.Ok,
            Summary = $"Apps und Desktop-Programme duerfen auf {consent.Kind} zugreifen.",
            Detail = detail.ToString().TrimEnd(),
        };
    }

    private static string Describe(string? value) => value switch
    {
        "Allow" => "erlaubt",
        "Deny" => "verweigert",
        null => "nicht gesetzt (Windows-Standard: erlaubt)",
        _ => value,
    };
}

/// <summary>Wiedergabe- und Aufnahmegeraete inklusive der deaktivierten und abgesteckten.</summary>
public sealed class AudioDeviceCheck : ICheck
{
    public string Name => "Audiogeraete";
    public string Category => "Ton";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.AudioDevice, Cause.Microphone, Cause.UsbDevice };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var endpoints = ctx.Profile.AudioEndpoints;

        if (endpoints.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "audio-endpoints", Category = Category, Title = "Audiogeraete nicht auslesbar",
                    Severity = Severity.Info,
                    Summary = "Die Geraeteliste konnte nicht aus der Registrierung gelesen werden.",
                    Recommendation = "Die Geraete stattdessen in den Sound-Einstellungen pruefen und dort " +
                                     "'Deaktivierte Geraete anzeigen' einschalten.",
                }
            });
        }

        findings.Add(Evaluate(endpoints.Where(e => !e.IsCapture).ToList(), isCapture: false));
        findings.Add(Evaluate(endpoints.Where(e => e.IsCapture).ToList(), isCapture: true));

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }

    private Finding Evaluate(List<AudioEndpoint> list, bool isCapture)
    {
        var kind = isCapture ? "Aufnahmegeraet" : "Wiedergabegeraet";
        var id = isCapture ? "audio-capture" : "audio-render";
        var cause = isCapture ? Cause.Microphone : Cause.AudioDevice;
        var symptoms = isCapture
            ? new[] { "mic-missing-system", "mic-not-in-app" }
            : new[] { "audio-no-sound" };

        var detail = new StringBuilder();
        foreach (var e in list.OrderByDescending(e => e.IsActive).ThenBy(e => e.Name, StringComparer.CurrentCulture))
            detail.AppendLine($"  {e.StateText,-22} {e.Name}");

        var active = list.Where(e => e.IsActive).ToList();
        var disabled = list.Where(e => e.IsDisabled).ToList();
        var unplugged = list.Where(e => e.IsUnplugged).ToList();

        if (list.Count == 0)
        {
            return new Finding
            {
                Id = id, Category = Category, Title = $"Kein {kind} im System",
                Severity = Severity.Warning,
                Summary = $"Windows fuehrt ueberhaupt kein {kind}.",
                Recommendation = isCapture
                    ? "Mikrofon oder Headset anschliessen und im Geraete-Manager pruefen, ob der Audiotreiber " +
                      "ueberhaupt geladen ist (Bereich 'Audioeingaenge und -ausgaenge')."
                    : "Im Geraete-Manager pruefen, ob der Audiotreiber geladen ist. Ohne Treiber gibt es keine Geraeteliste.",
                Causes = new Dictionary<Cause, double> { [cause] = 0.8, [Cause.DeviceDriver] = 0.5 },
                SymptomIds = symptoms,
            };
        }

        if (active.Count == 0)
        {
            var reason = disabled.Count > 0
                ? $"{disabled.Count} Geraet(e) sind deaktiviert"
                : $"{unplugged.Count} Geraet(e) sind nicht angeschlossen";

            return new Finding
            {
                Id = id, Category = Category, Title = $"Kein aktives {kind}",
                Severity = Severity.Critical,
                Summary = $"Es gibt {list.Count} bekannte(s) {kind}(e), aber keines ist aktiv - {reason}.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation =
                    "Sound-Einstellungen oeffnen, mit der rechten Maustaste in die Geraeteliste klicken und " +
                    "'Deaktivierte Geraete anzeigen' sowie 'Getrennte Geraete anzeigen' einschalten. Danach das " +
                    "gewuenschte Geraet ueber 'Aktivieren' zurueckholen.\n\n" +
                    "Steht das Geraet auf 'nicht angeschlossen', erkennt Windows an der Buchse nichts - dann sind " +
                    "Stecker, Kabel oder die Buchsenerkennung des Audiotreibers dran.",
                Causes = new Dictionary<Cause, double> { [cause] = 1.0, [Cause.DeviceDriver] = 0.3 },
                SymptomIds = symptoms,
                FixIds = new[] { "restart-audio-services" },
            };
        }

        if (disabled.Count > 0 || unplugged.Count > 0)
        {
            return new Finding
            {
                Id = id, Category = Category,
                Title = $"{kind}e vorhanden, einzelne sind aber deaktiviert oder abgesteckt",
                Severity = Severity.Warning,
                Summary = $"{active.Count} aktiv, {disabled.Count} deaktiviert, {unplugged.Count} nicht angeschlossen.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation =
                    $"Wenn das gesuchte {kind} in der Liste steht, aber nicht 'aktiv' ist, erklaert das genau das " +
                    "Symptom 'ist im System da, wird aber nirgends angeboten'. Deaktivierte Geraete sind in den " +
                    "Sound-Einstellungen standardmaessig unsichtbar - sie muessen dort erst eingeblendet und " +
                    "dann aktiviert werden.",
                Causes = new Dictionary<Cause, double> { [cause] = 0.7 },
                SymptomIds = symptoms,
            };
        }

        return new Finding
        {
            Id = id, Category = Category, Title = $"{kind}e sind in Ordnung",
            Severity = Severity.Ok,
            Summary = $"{active.Count} aktive(s) {kind}(e), keine deaktivierten Eintraege.",
            Detail = detail.ToString().TrimEnd(),
        };
    }
}

/// <summary>Der Windows-Datenschutz als Grund dafuer, dass eine App das Mikrofon nicht sieht.</summary>
public sealed class MicrophoneAccessCheck : ICheck
{
    public string Name => "Mikrofon-Berechtigungen";
    public string Category => "Ton";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.AppPermission, Cause.Microphone };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var finding = ConsentReport.Build(
            ctx.Profile.MicrophoneConsent,
            Category,
            "microphone",
            "Einstellungen > Datenschutz und Sicherheit > Mikrofon oeffnen. Dort muessen BEIDE Schalter an sein: " +
            "'Mikrofonzugriff' ganz oben und 'Desktop-Apps den Zugriff auf Ihr Mikrofon erlauben' ganz unten. " +
            "Der untere Schalter ist der entscheidende fuer Programme wie Discord, Teams oder OBS - er wird fast " +
            "immer uebersehen, weil er unterhalb der langen App-Liste steht.",
            Cause.Microphone,
            new[] { "mic-not-in-app", "mic-missing-system" },
            new[] { "mic-privacy-allow" });

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}

/// <summary>Audiodienste und Audio-Ereignisse im Protokoll.</summary>
public sealed class AudioServiceCheck : ICheck
{
    public string Name => "Audiodienste";
    public string Category => "Ton";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.AudioDevice, Cause.Microphone };

    private static readonly string[] AudioProviders =
    {
        "Microsoft-Windows-Audio", "Microsoft-Windows-Audio-AudioCore", "Audiosrv", "HdAudAddService",
    };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var services = ctx.Profile.Services;

        var relevant = new[] { "Audiosrv", "AudioEndpointBuilder" };
        var stopped = relevant
            .Where(n => services.TryGetValue(n, out var s) && !s.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var detail = new StringBuilder();
        foreach (var name in relevant)
            detail.AppendLine(services.TryGetValue(name, out var s)
                ? $"{name,-24} Zustand: {s.State}, Starttyp: {s.StartMode}"
                : $"{name,-24} nicht gefunden");

        findings.Add(stopped.Count > 0
            ? new Finding
            {
                Id = "audio-services", Category = Category, Title = "Ein Audiodienst laeuft nicht",
                Severity = Severity.Critical,
                Summary = "Nicht gestartet: " + string.Join(", ", stopped) + ".",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Ohne diese beiden Dienste gibt es weder Wiedergabe- noch Aufnahmegeraete - " +
                                 "Anwendungen melden dann schlicht 'kein Geraet gefunden'. Die Reparatur " +
                                 "'Audiodienste neu starten' setzt beide wieder in Gang.",
                Causes = new Dictionary<Cause, double> { [Cause.AudioDevice] = 0.9, [Cause.Microphone] = 0.9 },
                SymptomIds = new[] { "audio-no-sound", "mic-not-in-app", "mic-missing-system" },
                FixIds = new[] { "restart-audio-services" },
            }
            : new Finding
            {
                Id = "audio-services", Category = Category, Title = "Audiodienste laufen",
                Severity = Severity.Ok,
                Summary = "Windows-Audio und die Geraeteverwaltung sind gestartet.",
                Detail = detail.ToString().TrimEnd(),
            });

        var events = EventLogService.Query("System",
                EventLogService.XpathProviders(AudioProviders, ctx.LookbackDays), 60)
            .Where(e => e.Level is "Fehler" or "Kritisch" or "Warnung")
            .ToList();

        if (events.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "audio-events", Category = Category, Title = "Audio-Meldungen im Ereignisprotokoll",
                Severity = events.Count > 10 ? Severity.Warning : Severity.Info,
                Summary = $"{events.Count} Meldungen der Audiokomponenten in den letzten {ctx.LookbackDays} Tagen.",
                Detail = string.Join("\n", events.Take(20).Select(e => e.ToString())),
                Occurrences = events.Count,
                LastOccurrence = events.Max(e => e.Time),
                Recommendation = "Haeufen sich die Eintraege rund um die Aussetzer, ist der Audiotreiber der Verdaechtige. " +
                                 "Dann den Treiber des Mainboard-Herstellers (Realtek, nicht die Windows-Variante) neu installieren.",
                Causes = new Dictionary<Cause, double> { [Cause.AudioDevice] = 0.5, [Cause.DeviceDriver] = 0.3 },
                SymptomIds = new[] { "audio-crackle", "audio-no-sound" },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}
