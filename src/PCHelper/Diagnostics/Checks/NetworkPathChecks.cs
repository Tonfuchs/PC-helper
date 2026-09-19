using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PCHelper.Core;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Gemeinsame Werkzeuge der Netzwerk-Messungen: Weg ins Internet, Ping, DNS-Anfrage.</summary>
internal static class NetProbe
{
    /// <summary>Namensmuster bekannter VPN-Adapter.</summary>
    public static readonly Regex VpnPattern = new(
        "nordlynx|wireguard|openvpn|tap-windows|wintun|proton|mullvad|surfshark|cyberghost|expressvpn|vpn",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    public static bool IsVpn(NetworkInterface ni) => VpnPattern.IsMatch(ni.Name + " " + ni.Description);

    /// <summary>
    /// Ueber welchen Adapter geht der Verkehr ins Internet wirklich? Nicht der Adaptername entscheidet, ob ein VPN
    /// an ist - der NordLynx-Adapter bleibt "Up", solange die App laeuft -, sondern die Route: Windows selbst wird gefragt.
    /// </summary>
    public static NetworkInterface? BestInterfaceTo(IPAddress destination)
    {
        try
        {
            var addr = BitConverter.ToUInt32(destination.GetAddressBytes(), 0);
            if (GetBestInterface(addr, out var index) != 0) return null;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.GetIPProperties().GetIPv4Properties()?.Index == index) return ni;
                }
                catch (NetworkInformationException) { /* Adapter ohne IPv4 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Route ins Internet nicht ermittelbar: " + ex.Message);
        }
        return null;
    }

    public static IPAddress? Gateway(NetworkInterface ni)
    {
        try
        {
            return ni.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    /// <summary>Der echte Router: Gateway eines aktiven, physischen Adapters - nicht das des VPN.</summary>
    public static IPAddress? PhysicalGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVpn(ni)) continue;
            var gateway = Gateway(ni);
            if (gateway is not null) return gateway;
        }
        return null;
    }

    /// <summary>Mittlere Antwortzeit in Millisekunden, null wenn nichts zurueckkam.</summary>
    public static async Task<int?> PingAsync(string host, CancellationToken ct, int attempts = 4)
    {
        var times = new List<long>();
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1500);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException) { /* zaehlt als verloren */ }
        }
        return times.Count == 0 ? null : (int)Math.Round(times.Average());
    }

    // ---- DNS ueber UDP, damit sich einzelne Server gezielt messen lassen -------------------------------------

    private static byte[] BuildDnsQuery(string name, ushort id)
    {
        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        U16(id); U16(0x0100); U16(1); U16(0); U16(0); U16(0);   // Kopf: eine Frage, Rekursion gewuenscht
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        U16(1); U16(1);                                          // Typ A, Klasse IN
        return ms.ToArray();
    }

    /// <summary>Dauer einer Namensanfrage an genau diesen Server in Millisekunden, null bei Zeitueberschreitung.</summary>
    public static async Task<int?> DnsQueryAsync(IPAddress server, string name, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = BuildDnsQuery(name, id);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);

            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53));
            var answer = await udp.ReceiveAsync(cts.Token);
            sw.Stop();

            var b = answer.Buffer;
            return b.Length >= 12 && ((b[0] << 8) | b[1]) == id ? (int)sw.ElapsedMilliseconds : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}

/// <summary>
/// Wo sitzt die Bremse? Router, VPN-Einstieg und Internet getrennt zu messen zeigt, ob es im Haus liegt,
/// am VPN-Umweg oder beim Anbieter.
/// </summary>
public sealed class NetworkPathCheck : ICheck
{
    public string Name => "Weg ins Internet";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Wifi, Cause.Software };

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        var best = NetProbe.BestInterfaceTo(IPAddress.Parse("1.1.1.1"));
        bool vpn = best is not null && NetProbe.IsVpn(best);

        var router = NetProbe.PhysicalGateway();
        var vpnGateway = vpn ? NetProbe.Gateway(best!) : null;

        var pingRouter = router is null ? null : await NetProbe.PingAsync(router.ToString(), ct);
        var pingVpn = vpnGateway is null ? null : await NetProbe.PingAsync(vpnGateway.ToString(), ct);
        var pingNet = await NetProbe.PingAsync("1.1.1.1", ct);

        var numbers = new StringBuilder();
        numbers.AppendLine($"Bis zum eigenen Router ({router?.ToString() ?? "nicht ermittelbar"}): {Ms(pingRouter)}");
        if (vpn) numbers.AppendLine($"Bis zum VPN-Einstieg ({vpnGateway?.ToString() ?? "keine Gegenstelle bekannt"}): {Ms(pingVpn)}");
        numbers.AppendLine($"Ins Internet (1.1.1.1): {Ms(pingNet)}");
        if (best is not null) numbers.AppendLine($"Der Verkehr ins Internet laeuft ueber: {best.Name} ({best.Description})");

        // Der Beweis: schnelles Heimnetz, langsamer Weg ins VPN - dann sind weder Router noch Anschluss schuld.
        bool proof = pingRouter is < 10 && pingVpn is > 25;

        if (vpn)
        {
            findings.Add(new Finding
            {
                Id = "net-path",
                Category = Category,
                Title = proof ? "Der VPN-Umweg kostet spuerbar Zeit" : "Ein VPN nimmt den gesamten Verkehr",
                Severity = proof ? Severity.Warning : Severity.Info,
                Summary = proof
                    ? $"Dein Heimnetz antwortet in {pingRouter} ms, der Weg ins VPN kostet {pingVpn} ms. Damit ist bewiesen: weder dein " +
                      "Router noch dein Internetanschluss sind schuld, sondern der Umweg."
                    : $"Der Internetverkehr laeuft ueber '{best!.Name}'. Jede Anfrage geht zuerst zu einem fremden Server und erst von dort " +
                      "weiter zum Ziel.",
                Detail = numbers.ToString().TrimEnd() +
                         "\n\nEin Server im eigenen Land kostet rund 10 ms Aufschlag, einer im Nachbarland um 20 ms, einer auf einem " +
                         "anderen Kontinent ueber 100 ms. Diesen Aufschlag zahlt jede einzelne Anfrage, bevor sie ueberhaupt losgeht.",
                Recommendation = proof
                    ? "VPN einmal ausschalten und die Messung wiederholen - der Unterschied ist meist sofort sichtbar. Wenn du das VPN " +
                      "brauchst: einen Standort im eigenen Land waehlen, und auf Doppel-VPN (zwei Server hintereinander) verzichten."
                    : "Bei einem VPN ist das gewollt. Wenn Seiten mit vielen kleinen Bildern quaelend langsam laden, das VPN einmal " +
                      "abschalten und hier erneut messen.",
                Causes = proof
                    ? new Dictionary<Cause, double> { [Cause.Network] = 0.5, [Cause.Software] = 0.5 }
                    : new Dictionary<Cause, double>(),
                SymptomIds = new[] { "net-slow" },
            });
        }
        else if (best is not null)
        {
            findings.Add(new Finding
            {
                Id = "net-path", Category = Category, Title = "Kein VPN im Weg",
                Severity = Severity.Ok,
                Summary = $"Der Internetverkehr geht ohne Umweg ueber '{best.Name}' hinaus.",
                Detail = numbers.ToString().TrimEnd(),
            });
        }

        if (pingRouter is not null)
        {
            var slow = pingRouter > 30;
            findings.Add(new Finding
            {
                Id = "net-router-latency", Category = Category,
                Title = slow ? "Schon der eigene Router antwortet traege" : "Das Heimnetz antwortet schnell",
                Severity = slow ? Severity.Warning : Severity.Ok,
                Summary = slow
                    ? $"{pingRouter} ms bis zum eigenen Router. Dann liegt das Problem im Haus - nicht beim Anbieter."
                    : $"{pingRouter} ms bis zum eigenen Router.",
                Detail = numbers.ToString().TrimEnd(),
                Recommendation = slow
                    ? "Schlechtes WLAN, ein defektes Netzwerkkabel oder ein ueberlasteter Router sind die haeufigsten Gruende. " +
                      "Testweise per Kabel verbinden, Kabel und Router-Anschluss tauschen, den Router einmal stromlos machen."
                    : null,
                Causes = slow
                    ? new Dictionary<Cause, double> { [Cause.Wifi] = 0.7, [Cause.Network] = 0.6 }
                    : new Dictionary<Cause, double>(),
                SymptomIds = new[] { "net-slow", "net-drops" },
            });
        }

        return findings;
    }

    private static string Ms(int? value) => value is null ? "keine Antwort" : $"{value} ms";
}

/// <summary>
/// Die Kernmessung hinter "der Steam-Workshop laedt ewig": Eine Galerieseite besteht aus vielen kleinen Bildern, und
/// jedes braucht einen eigenen Verbindungsaufbau. Gemessen wird, was ein einzelner kleiner Abruf inklusive
/// Verbindungsaufbau und Verschluesselung kostet - jedes Mal mit einer frischen Verbindung.
/// </summary>
public sealed class PageLoadCheck : ICheck
{
    public string Name => "Ladezeit einzelner Abrufe";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Wifi, Cause.Software };

    /// <summary>Winzige Ressourcen dreier grosser Anbieter. Es geht nur um Zeit, geladen werden wenige Kilobyte.</summary>
    private static readonly (string Name, string Url)[] Sources =
    {
        ("Wikipedia", "https://www.wikipedia.org/static/favicon/wikipedia.ico"),
        ("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace"),
        ("Google", "https://www.google.com/favicon.ico"),
    };

    private const int Passes = 4;

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(30));

        var perSource = new List<(string Name, int Ms)>();
        try
        {
            foreach (var (name, url) in Sources)
            {
                var times = new List<int>();
                for (int i = 0; i < Passes; i++)
                {
                    var ms = await FetchAsync(url, overall.Token);
                    if (ms is not null) times.Add(ms.Value);
                }

                // Der erste Durchgang ist immer langsamer (kalter Start) und zaehlt nicht mit.
                if (times.Count > 1) perSource.Add((name, (int)Math.Round(times.Skip(1).Average())));

                // Kommt beim ersten Anbieter gar nichts zurueck, ist die Leitung weg - das melden andere Pruefungen.
                if (times.Count == 0 && perSource.Count == 0) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("Ladezeitmessung nach 30 Sekunden abgebrochen.");
        }

        if (perSource.Count == 0) return Array.Empty<Finding>();

        var average = (int)Math.Round(perSource.Average(s => s.Ms));
        var pageSeconds = Math.Round(average * 60 / 6 / 1000.0, 1);
        var severity = average > 300 ? Severity.Warning : average > 150 ? Severity.Info : Severity.Ok;

        var detail = new StringBuilder();
        detail.AppendLine("Gemessen wird ein einzelner kleiner Abruf mit jedes Mal frischer Verbindung (Namensaufloesung, Verbindungsaufbau,");
        detail.AppendLine("Verschluesselung, Antwort). Uebertragen werden nur wenige Kilobyte von diesen Servern:");
        detail.AppendLine();
        foreach (var (name, ms) in perSource) detail.AppendLine($"  {name}: {ms} ms");
        detail.AppendLine();
        detail.AppendLine($"Hochgerechnet auf eine Seite mit 60 Vorschaubildern und 6 gleichzeitigen Verbindungen: rund {pageSeconds} Sekunden.");

        return new[]
        {
            new Finding
            {
                Id = "net-pageload", Category = Category,
                Title = severity == Severity.Ok ? "Einzelne Abrufe laden zuegig" : "Einzelne Abrufe dauern lange",
                Severity = severity,
                Summary = $"Ein einzelner kleiner Abruf kostet {average} ms. Eine Seite mit 60 Bildern braucht damit rund {pageSeconds} Sekunden.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = severity == Severity.Ok
                    ? null
                    : "Nicht die Leitung ist zu duenn, sondern der Verbindungsaufbau dauert zu lange. Ohne Umweg sind 40 bis 80 ms " +
                      "normal. Meist stecken ein VPN, eine hohe Grundlatenz (WLAN) oder eine langsame Namensaufloesung dahinter - " +
                      "siehe die Befunde zum Weg ins Internet und zur Namensaufloesung.",
                Causes = severity == Severity.Ok
                    ? new Dictionary<Cause, double>()
                    : new Dictionary<Cause, double> { [Cause.Network] = 0.6, [Cause.Wifi] = 0.4, [Cause.Software] = 0.3 },
                SymptomIds = new[] { "net-slow" },
            }
        };
    }

    private static async Task<int?> FetchAsync(string url, CancellationToken ct)
    {
        // Ein eigener Handler je Abruf erzwingt eine frische Verbindung - genau das kostet bei vielen kleinen Bildern.
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.Zero };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCHelper");

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, ct);
            await response.Content.ReadAsByteArrayAsync(ct);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Ladezeitmessung: {url} nicht abrufbar ({ex.GetBaseException().Message})");
            return null;
        }
    }
}

/// <summary>
/// Namensaufloesung: Bevor eine Seite ueberhaupt laedt, muss ihr Name in eine Nummer uebersetzt werden. Eine normale
/// Webseite fragt dabei 10 bis 30 verschiedene Namen ab - dauert jede Anfrage lange, "haengt" die Seite erst sekundenlang.
/// </summary>
public sealed class DnsSpeedCheck : ICheck
{
    public string Name => "Tempo der Namensaufloesung";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Software };

    private static readonly string[] Names = { "steamcommunity.com", "cdn.discordapp.com", "www.twitch.tv", "www.google.com" };

    private static readonly (string Name, string Ip)[] Public =
    {
        ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9"),
    };

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var configured = ctx.Profile.NetworkAdapters
            .Where(a => a.IsConnected)
            .SelectMany(a => a.DnsServers)
            .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .Where(ip => ip is { AddressFamily: AddressFamily.InterNetwork })
            .Select(ip => ip!)
            .DistinctBy(ip => ip.ToString())
            .Take(2)
            .ToList();

        if (configured.Count == 0) return Array.Empty<Finding>();

        var detail = new StringBuilder();
        var all = new List<int>();

        foreach (var server in configured)
        {
            var times = new List<int>();
            foreach (var name in Names)
            {
                var ms = await NetProbe.DnsQueryAsync(server, name, ct);
                if (ms is not null) times.Add(ms.Value);
            }
            all.AddRange(times);
            detail.AppendLine(times.Count == 0
                ? $"Eingetragener Server {server}: antwortet nicht"
                : $"Eingetragener Server {server}: im Schnitt {(int)Math.Round(times.Average())} ms (langsamste {times.Max()} ms, {times.Count} von {Names.Length} Anfragen beantwortet)");
        }

        // Direktvergleich: so schnell waere ein anderer Server. Zeigt auch, ob ein VPN alle anderen blockiert.
        detail.AppendLine();
        detail.AppendLine("Zum Vergleich (Anfrage fuer www.wikipedia.org):");

        var gateway = NetProbe.PhysicalGateway();
        var candidates = new List<(string Name, string Ip)>();
        if (gateway is not null) candidates.Add(("Eigener Router", gateway.ToString()));
        candidates.AddRange(Public);

        int reachable = 0;
        foreach (var (name, ip) in candidates)
        {
            var ms = await NetProbe.DnsQueryAsync(IPAddress.Parse(ip), "www.wikipedia.org", ct);
            if (ms is not null) reachable++;
            detail.AppendLine($"  {name} ({ip}): {(ms is null ? "nicht erreichbar" : ms + " ms")}");
        }

        var best = NetProbe.BestInterfaceTo(IPAddress.Parse("1.1.1.1"));
        bool vpn = best is not null && NetProbe.IsVpn(best);
        if (reachable == 0 && vpn)
            detail.AppendLine("\nKein anderer Namensserver ist erreichbar: Das VPN zwingt allen Namensverkehr durch seinen eigenen Server. " +
                              "Das ist gewollt (Datenschutz) - heisst aber auch, dass sich die Aufloesung nicht beschleunigen laesst, solange das VPN laeuft.");

        if (all.Count == 0)
        {
            return new[]
            {
                new Finding
                {
                    Id = "net-dns-speed", Category = Category, Title = "Der eingetragene Namensserver antwortet nicht",
                    Severity = Severity.Warning,
                    Summary = "Auf direkte Anfragen kam vom eingetragenen Namensserver keine Antwort.",
                    Detail = detail.ToString().TrimEnd(),
                    Recommendation = "Manche Router und VPN-Programme beantworten direkte Anfragen nicht - dann ist das harmlos, solange " +
                                     "Webseiten laden. Klappt die normale Namensaufloesung ebenfalls nicht, zuerst den DNS-Zwischenspeicher " +
                                     "leeren und testweise einen anderen Namensserver (z. B. 1.1.1.1) eintragen.",
                    Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.4 },
                    SymptomIds = new[] { "net-slow", "net-no-internet" },
                    FixIds = new[] { "flush-dns" },
                }
            };
        }

        var average = (int)Math.Round(all.Average());
        var severity = average > 120 ? Severity.Warning : average > 60 ? Severity.Info : Severity.Ok;

        return new[]
        {
            new Finding
            {
                Id = "net-dns-speed", Category = Category,
                Title = severity == Severity.Ok ? "Namen werden zuegig aufgeloest" : "Die Namensaufloesung ist langsam",
                Severity = severity,
                Summary = $"Eine Namensanfrage dauert im Schnitt {average} ms." +
                          (severity == Severity.Ok ? "" : " Eine normale Webseite fragt 10 bis 30 verschiedene Namen ab - das summiert sich."),
                Detail = detail.ToString().TrimEnd(),
                Recommendation = severity == Severity.Ok
                    ? null
                    : "Einen schnellen Namensserver eintragen (Cloudflare 1.1.1.1 oder den Router selbst) und den DNS-Zwischenspeicher " +
                      "leeren. Laeuft ein VPN, kommt die Verzoegerung meist daher.",
                FixIds = severity == Severity.Ok ? Array.Empty<string>() : new[] { "flush-dns" },
                Causes = severity == Severity.Ok
                    ? new Dictionary<Cause, double>()
                    : new Dictionary<Cause, double> { [Cause.Network] = 0.5, [Cause.Software] = 0.3 },
                SymptomIds = new[] { "net-slow" },
            }
        };
    }
}
