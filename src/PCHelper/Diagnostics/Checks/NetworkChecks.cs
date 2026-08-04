using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Zustand der Netzwerkadapter und ihrer IP-Konfiguration.</summary>
public sealed class NetworkAdapterCheck : ICheck
{
    public string Name => "Netzwerkadapter";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Wifi, Cause.DeviceDriver };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var adapters = ctx.Profile.NetworkAdapters;

        if (adapters.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "net-adapters", Category = Category, Title = "Kein Netzwerkadapter gefunden",
                    Severity = Severity.Critical,
                    Summary = "Windows meldet keinen physischen Netzwerkadapter.",
                    Recommendation = "Im Geraete-Manager unter 'Netzwerkadapter' nachsehen. Fehlt dort alles oder steht " +
                                     "ein Ausrufezeichen daneben, fehlt der Treiber - er muss dann von einem anderen " +
                                     "Rechner geholt und per USB-Stick eingespielt werden (Chipsatz-/LAN-Treiber des Mainboards).",
                    Causes = new Dictionary<Cause, double> { [Cause.Network] = 1.0, [Cause.DeviceDriver] = 0.8 },
                    SymptomIds = new[] { "net-no-internet" },
                }
            });
        }

        var detail = new StringBuilder();
        foreach (var a in adapters)
        {
            detail.AppendLine($"{a.ConnectionName} ({a.Name})");
            detail.AppendLine($"    Status: {a.StatusText}, aktiviert: {(a.Enabled ? "ja" : "nein")}, " +
                              $"Verbindungsgeschwindigkeit: {a.SpeedText}");
            detail.AppendLine($"    IP: {(a.IpAddresses.Count == 0 ? "keine" : string.Join(", ", a.IpAddresses))}");
            detail.AppendLine($"    Gateway: {(string.IsNullOrEmpty(a.Gateway) ? "keins" : a.Gateway)}   " +
                              $"DNS: {(a.DnsServers.Count == 0 ? "keine" : string.Join(", ", a.DnsServers))}   " +
                              $"DHCP: {(a.DhcpEnabled ? "an" : "aus")}");
        }

        var connected = adapters.Where(a => a.IsConnected).ToList();
        var withIp = adapters.Where(a => a.HasUsableIpv4).ToList();
        var apipa = adapters.Where(a => a.HasApipaAddress).ToList();

        if (connected.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "net-adapters", Category = Category, Title = "Kein Netzwerkadapter ist verbunden",
                Severity = Severity.Critical,
                Summary = string.Join("; ", adapters.Select(a => $"{a.ConnectionName}: {a.StatusText}")),
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Bei 'Medium getrennt' steckt das Kabel nicht oder die Gegenstelle ist aus - " +
                                 "beide Stecker pruefen und auf die Leuchtdioden am Anschluss achten. Bei " +
                                 "'Hardware deaktiviert' ist der Adapter im Geraete-Manager oder ueber einen " +
                                 "Funkschalter abgeschaltet.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 1.0, [Cause.Wifi] = 0.4 },
                SymptomIds = new[] { "net-no-internet", "net-drops" },
            });
        }
        else if (apipa.Count > 0 && withIp.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "net-adapters", Category = Category, Title = "Keine gueltige IP-Adresse (DHCP antwortet nicht)",
                Severity = Severity.Critical,
                Summary = "Der Adapter hat sich selbst eine 169.254er-Adresse gegeben - das heisst: der Router " +
                          "hat keine Adresse vergeben.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Router neu starten und pruefen, ob dort DHCP aktiv ist. Danach den Netzwerkstapel " +
                                 "zuruecksetzen (Reparatur 'Netzwerkeinstellungen zuruecksetzen'). Bleibt es dabei, " +
                                 "ein anderes Kabel und einen anderen Anschluss am Router testen.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 1.0 },
                SymptomIds = new[] { "net-no-internet" },
                FixIds = new[] { "network-reset-stack" },
            });
        }
        else
        {
            findings.Add(new Finding
            {
                Id = "net-adapters", Category = Category, Title = "Netzwerkadapter ist verbunden",
                Severity = Severity.Ok,
                Summary = string.Join("; ", connected.Select(a =>
                    $"{a.ConnectionName} ({a.SpeedText}, {a.IpAddresses.FirstOrDefault(i => i.Contains('.')) ?? "keine IPv4"})")),
                Detail = detail.ToString().TrimEnd(),
            });
        }

        // Ein Gigabit-Adapter, der mit 100 Mbit aushandelt, ist ein klassischer Kabelfehler.
        var slowLink = connected.FirstOrDefault(a => !a.IsWireless && a.SpeedBitsPerSecond is > 0 and <= 100_000_000);
        if (slowLink is not null)
        {
            findings.Add(new Finding
            {
                Id = "net-linkspeed", Category = Category, Title = "Kabelverbindung laeuft nur mit 100 Mbit/s",
                Severity = Severity.Warning,
                Summary = $"{slowLink.ConnectionName} hat {slowLink.SpeedText} ausgehandelt.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Fast alle heutigen Anschluesse koennen 1 Gbit/s. Wird nur 100 Mbit/s ausgehandelt, " +
                                 "liegt es meist an einem beschaedigten Kabel, einer schlechten Steckverbindung oder " +
                                 "einem alten Switch. Anderes Kabel (mindestens Cat 5e) und anderen Port testen.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.6 },
                SymptomIds = new[] { "net-slow" },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>
/// Aktiver Erreichbarkeitstest: Router, Internet und Namensaufloesung getrennt
/// pruefen. Erst diese Dreiteilung sagt, wo die Kette reisst.
/// </summary>
public sealed class NetworkReachabilityCheck : ICheck
{
    public string Name => "Erreichbarkeit";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Wifi };

    /// <summary>Oeffentlicher DNS-Server, per IP angesprochen - damit der Test ohne Namensaufloesung auskommt.</summary>
    private const string PublicIp = "1.1.1.1";

    private const string TestHost = "www.msftconnecttest.com";

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        var gateway = ctx.Profile.NetworkAdapters
            .Where(a => a.IsConnected && !string.IsNullOrWhiteSpace(a.Gateway))
            .Select(a => a.Gateway.Split(',')[0].Trim())
            .FirstOrDefault();

        var gatewayResult = gateway is null ? null : await PingAsync(gateway, ct);
        var internetResult = await PingAsync(PublicIp, ct);
        var (dnsOk, dnsMessage, dnsMs) = await ResolveAsync(TestHost, ct);

        var detail = new StringBuilder();
        detail.AppendLine("Dieser Test sendet einige wenige Pakete an den eigenen Router und an einen oeffentlichen");
        detail.AppendLine("Namensserver. Er veraendert nichts und uebertraegt keine persoenlichen Daten.");
        detail.AppendLine();
        detail.AppendLine($"Router ({gateway ?? "nicht ermittelbar"}):   {Describe(gatewayResult)}");
        detail.AppendLine($"Internet ({PublicIp}):            {Describe(internetResult)}");
        detail.AppendLine($"Namensaufloesung ({TestHost}): {dnsMessage}");

        // Die Reihenfolge der Auswertung entspricht der Reihenfolge der Kette.
        if (gateway is not null && gatewayResult is { Received: 0 })
        {
            findings.Add(new Finding
            {
                Id = "net-reach", Category = Category, Title = "Der eigene Router antwortet nicht",
                Severity = Severity.Critical,
                Summary = $"Kein einziges Paket an {gateway} kam zurueck - die Verbindung endet schon im eigenen Netz.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Damit ist alles jenseits des Routers erst einmal unwichtig. Kabel bzw. WLAN-Verbindung, " +
                                 "Router-Stromversorgung und eine eventuell aktive Drittanbieter-Firewall pruefen. " +
                                 "Manche Router beantworten Ping grundsaetzlich nicht - dann ist dieser Punkt nur " +
                                 "aussagekraeftig, wenn auch der Internettest scheitert.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 1.0, [Cause.Wifi] = 0.4, [Cause.Software] = 0.3 },
                SymptomIds = new[] { "net-no-internet", "net-drops" },
                FixIds = new[] { "network-reset-stack" },
            });
        }
        else if (internetResult.Received == 0)
        {
            findings.Add(new Finding
            {
                Id = "net-reach", Category = Category, Title = "Der Router ist erreichbar, das Internet nicht",
                Severity = Severity.Critical,
                Summary = $"Das eigene Netz funktioniert, aber {PublicIp} antwortet nicht.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Das spricht fuer den Anschluss oder den Router, nicht fuer diesen PC. Am Router " +
                                 "nachsehen, ob eine Internetverbindung besteht, und andere Geraete im selben Netz " +
                                 "gegentesten. Ebenfalls pruefen: VPN-Software, die den gesamten Verkehr umleitet.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.9, [Cause.Software] = 0.4 },
                SymptomIds = new[] { "net-no-internet" },
            });
        }
        else if (!dnsOk)
        {
            findings.Add(new Finding
            {
                Id = "net-reach", Category = Category, Title = "Internet erreichbar, aber die Namensaufloesung scheitert",
                Severity = Severity.Critical,
                Summary = "IP-Adressen sind erreichbar, Webadressen lassen sich nicht aufloesen - ein reines DNS-Problem.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Genau dieses Muster erzeugt das Gefuehl 'Internet geht nicht', obwohl die Leitung " +
                                 "steht. Zuerst den DNS-Zwischenspeicher leeren (Reparatur 'DNS-Zwischenspeicher leeren'). " +
                                 "Bleibt es dabei, testweise einen anderen Namensserver eintragen oder den Router neu starten.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.8, [Cause.Software] = 0.3 },
                SymptomIds = new[] { "net-no-internet", "net-slow" },
                FixIds = new[] { "flush-dns" },
            });
        }
        else
        {
            var latency = internetResult.AverageMs;
            var lossy = internetResult.Received < internetResult.Sent;
            var slow = latency > 80;

            findings.Add(new Finding
            {
                Id = "net-reach", Category = Category,
                Title = lossy || slow ? "Verbindung steht, ist aber auffaellig" : "Verbindung ist in Ordnung",
                Severity = lossy ? Severity.Warning : slow ? Severity.Info : Severity.Ok,
                Summary = $"Antwortzeit ins Internet {latency:0} ms, {internetResult.Received} von " +
                          $"{internetResult.Sent} Paketen angekommen, Namensaufloesung in {dnsMs:0} ms.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = lossy
                    ? "Paketverluste schon auf dem kurzen Weg sind ein deutliches Zeichen - bei WLAN fuer Stoerungen " +
                      "oder zu grosse Entfernung, bei Kabel fuer eine schlechte Steckverbindung. Zum Eingrenzen einmal " +
                      "die jeweils andere Verbindungsart testen."
                    : slow
                        ? "Ueber 80 ms sind fuer Spiele und Videokonferenzen spuerbar. Bei WLAN lohnt der Vergleich " +
                          "mit einer Kabelverbindung; bleibt es hoch, liegt es am Anschluss."
                        : null,
                Causes = lossy || slow
                    ? new Dictionary<Cause, double> { [Cause.Network] = 0.5, [Cause.Wifi] = 0.4 }
                    : new Dictionary<Cause, double>(),
                SymptomIds = new[] { "net-slow", "net-drops" },
            });
        }

        return findings;
    }

    private static string Describe(PingSummary? result) => result is null
        ? "uebersprungen (kein Standardgateway bekannt)"
        : result.Received == 0
            ? $"keine Antwort ({result.Sent} Versuche)"
            : $"{result.Received}/{result.Sent} Pakete, im Mittel {result.AverageMs:0} ms";

    private sealed record PingSummary(int Sent, int Received, double AverageMs);

    private static async Task<PingSummary> PingAsync(string host, CancellationToken ct)
    {
        const int attempts = 4;
        int received = 0;
        double total = 0;

        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1500);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    total += reply.RoundtripTime;
                }
            }
            catch
            {
                // Nicht aufloesbar oder blockiert - zaehlt als verlorenes Paket.
            }
        }

        return new PingSummary(attempts, received, received == 0 ? 0 : total / received);
    }

    private static async Task<(bool Ok, string Message, double Ms)> ResolveAsync(string host, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            return addresses.Length > 0
                ? (true, $"aufgeloest auf {addresses[0]} ({ms:0} ms)", ms)
                : (false, "keine Adresse zurueckgeliefert", ms);
        }
        catch (Exception ex)
        {
            return (false, "fehlgeschlagen - " + ex.Message, (DateTime.UtcNow - started).TotalMilliseconds);
        }
    }
}

/// <summary>Netzwerkbezogene Ereignisse: Abbrueche, DHCP-Fehler, WLAN-Trennungen.</summary>
public sealed class NetworkEventCheck : ICheck
{
    public string Name => "Netzwerk-Ereignisse";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Wifi, Cause.DeviceDriver };

    private static readonly string[] Providers =
    {
        "Tcpip", "Tcpip6", "Microsoft-Windows-Dhcp-Client", "Microsoft-Windows-Dhcpv6-Client",
        "Microsoft-Windows-DNS-Client", "Microsoft-Windows-NDIS", "Microsoft-Windows-WLAN-AutoConfig",
        "Microsoft-Windows-NetworkProfile",
    };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var events = EventLogService.Query("System",
                EventLogService.XpathProviders(Providers, ctx.LookbackDays), 120)
            .Where(e => e.Level is "Fehler" or "Kritisch" or "Warnung")
            .ToList();

        if (events.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "net-events", Category = Category, Title = "Keine Netzwerkfehler protokolliert",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen sind keine Netzwerkfehler aufgelaufen.",
                }
            });
        }

        var byProvider = events.GroupBy(e => e.Provider).OrderByDescending(g => g.Count()).ToList();
        var wlanDrops = events.Count(e => e.Provider.Contains("WLAN", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        foreach (var g in byProvider)
            sb.AppendLine($"{g.Count(),4}x  {g.Key} - zuletzt {g.Max(e => e.Time):dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine(string.Join("\n", events.Take(20).Select(e => e.ToString())));

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "net-events", Category = Category, Title = "Netzwerkfehler im Ereignisprotokoll",
                Severity = events.Count > 20 ? Severity.Warning : Severity.Info,
                Summary = $"{events.Count} Meldungen in den letzten {ctx.LookbackDays} Tagen, " +
                          $"haeufigste Quelle: {byProvider[0].Key}." +
                          (wlanDrops > 0 ? $" Davon {wlanDrops} aus dem WLAN-Dienst." : ""),
                Detail = sb.ToString().TrimEnd(),
                Occurrences = events.Count,
                LastOccurrence = events.Max(e => e.Time),
                Recommendation = "Wiederkehrende Eintraege im gleichen Rhythmus deuten auf Energiesparen am Adapter " +
                                 "oder einen instabilen Treiber hin. Zuerst im Geraete-Manager beim Adapter unter " +
                                 "'Energieverwaltung' das automatische Abschalten unterbinden.",
                Causes = new Dictionary<Cause, double>
                {
                    [Cause.Network] = 0.6,
                    [Cause.Wifi] = wlanDrops > 0 ? 0.6 : 0.1,
                    [Cause.DeviceDriver] = 0.3,
                },
                SymptomIds = new[] { "net-drops", "net-no-internet" },
                FixIds = new[] { "network-power-off" },
            }
        });
    }
}
