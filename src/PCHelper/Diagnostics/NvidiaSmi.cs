using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PCHelper.Core;

namespace PCHelper.Diagnostics;

/// <summary>Momentaufnahme der GPU-Sensorik.</summary>
public sealed record GpuSample(
    string Name,
    string DriverVersion,
    double? TemperatureC,
    double? HotspotC,
    double? UtilizationPercent,
    double? PowerWatt,
    double? PowerLimitWatt,
    double? ClockMhz,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    string? PerformanceState,
    double? FanPercent = null,
    double? MemoryUtilPercent = null,
    double? EncoderUtilPercent = null,
    double? DecoderUtilPercent = null,
    int? PcieLinkGenCurrent = null,
    int? PcieLinkGenMax = null,
    int? PcieLinkWidthCurrent = null,
    int? PcieLinkWidthMax = null,
    bool? ThrottlePowerCap = null,
    bool? ThrottleThermal = null,
    bool? ThrottleHwSlowdown = null)
{
    /// <summary>Belegter Grafikspeicher als Anteil (0..1), oder null wenn unbekannt.</summary>
    public double? MemoryLoadPercent =>
        MemoryUsedMb is { } used && MemoryTotalMb is > 0 ? used / MemoryTotalMb.Value * 100 : null;

    /// <summary>Ob eine Drosselungsursache aktiv gemeldet ist.</summary>
    public bool IsThrottled => ThrottlePowerCap == true || ThrottleThermal == true || ThrottleHwSlowdown == true;

    /// <summary>Ob der PCIe-Link unter seinem Maximum laeuft (schlechter Sitz, falscher Slot, Riser-Kabel).</summary>
    public bool IsPcieLinkDegraded =>
        (PcieLinkGenCurrent is { } g && PcieLinkGenMax is { } gm && g < gm) ||
        (PcieLinkWidthCurrent is { } w && PcieLinkWidthMax is { } wm && w < wm);
}

/// <summary>Ein Prozess, der gerade Grafikspeicher belegt (aus nvidia-smi, VRAM-genau statt der unzuverlaessigen Prozentwerte des Task-Managers).</summary>
public sealed record GpuProcessSample(int Pid, string ProcessName, double UsedMemoryMb);

/// <summary>
/// Auslesen der NVIDIA-GPU ueber nvidia-smi (liegt bei installiertem Treiber
/// in System32). Fuer AMD/Intel gibt es kein Aequivalent - dort bleibt die
/// Sensorik leer, alle anderen Pruefungen laufen trotzdem.
/// </summary>
public static class NvidiaSmi
{
    private static string? _path;
    private static bool _resolved;

    /// <summary>Pfad zu nvidia-smi.exe, oder null wenn nicht vorhanden.</summary>
    public static string? Path
    {
        get
        {
            if (_resolved) return _path;
            _resolved = true;

            var candidates = new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            };
            _path = candidates.FirstOrDefault(File.Exists);
            return _path;
        }
    }

    public static bool IsAvailable => Path is not null;

    // Feldnamen je Wert, in der Reihenfolge der Abfrage. Mehrere Namen stehen fuer dasselbe Feld bei
    // verschiedenen Treiberstaenden: Neuere nvidia-smi kennen die Drosselungsfelder nur noch als
    // "clocks_event_reasons.*" (Treiber 616.92 lehnt "clocks_throttle_reasons.thermal" ab), aeltere nur
    // als "clocks_throttle_reasons.*". Kennt nvidia-smi einen Namen nicht, scheitert die GANZE Abfrage -
    // ohne Ausweichen gaebe es dann weder Live-Werte noch gpu-blackbox.csv.
    private static readonly string[][] Fields =
    {
        new[] { "name" }, new[] { "driver_version" }, new[] { "temperature.gpu" }, new[] { "utilization.gpu" },
        new[] { "power.draw" }, new[] { "power.limit" }, new[] { "clocks.current.graphics" },
        new[] { "memory.used" }, new[] { "memory.total" }, new[] { "pstate" },
        new[] { "fan.speed" }, new[] { "utilization.memory" }, new[] { "utilization.encoder" }, new[] { "utilization.decoder" },
        new[] { "pcie.link.gen.current" }, new[] { "pcie.link.gen.max" },
        new[] { "pcie.link.width.current" }, new[] { "pcie.link.width.max" },
        new[] { "clocks_event_reasons.sw_power_cap", "clocks_throttle_reasons.sw_power_cap" },
        new[] { "clocks_event_reasons.sw_thermal_slowdown", "clocks_throttle_reasons.sw_thermal_slowdown" },
        new[] { "clocks_event_reasons.hw_slowdown", "clocks_throttle_reasons.hw_slowdown" },
    };

    private const int IName = 0, IDriver = 1, ITemp = 2, IUtil = 3, IPower = 4, IPowerLimit = 5, IClock = 6,
        IMemUsed = 7, IMemTotal = 8, IPState = 9, IFan = 10, IMemUtil = 11, IEnc = 12, IDec = 13,
        IGenCur = 14, IGenMax = 15, IWidthCur = 16, IWidthMax = 17, IThrPower = 18, IThrThermal = 19, IThrHw = 20;

    private static readonly Regex InvalidField = new("Field \"([^\"]+)\" is not a valid field to query", RegexOptions.IgnoreCase);
    private static readonly object FieldLock = new();

    // Je Feld der Index des Namens, der beim Treiber funktioniert; ausserhalb der Liste = Feld entfaellt.
    private static readonly int[] Chosen = new int[Fields.Length];

    /// <summary>
    /// Fehlertext des letzten fehlgeschlagenen Abrufs (z. B. "Unable to determine the device handle ...",
    /// wenn die Karte nicht mehr antwortet), null nach einem erfolgreichen Abruf.
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>Liest die aktuellen Werte aller NVIDIA-GPUs. Leere Liste, wenn nicht verfuegbar.</summary>
    public static async Task<IReadOnlyList<GpuSample>> SampleAsync(CancellationToken ct = default)
    {
        if (Path is null) return Array.Empty<GpuSample>();

        // Ein unbekannter Feldname kostet nur dieses Feld: nvidia-smi nennt ihn in der Fehlermeldung,
        // er wird durch den naechsten Namen ersetzt bzw. weggelassen, dann wird neu abgefragt.
        for (var attempt = 0; attempt <= Fields.Length + 1; attempt++)
        {
            int[] active;
            lock (FieldLock)
                active = Enumerable.Range(0, Fields.Length).Where(i => Chosen[i] < Fields[i].Length).ToArray();
            var query = string.Join(',', active.Select(i => Fields[i][Chosen[i]]));

            var r = await Shell.RunAsync(Path, $"--query-gpu={query} --format=csv,noheader,nounits", 10_000, ct);
            if (r.Success)
            {
                LastError = null;
                return Parse(r.StdOut, active);
            }

            var text = r.Combined.Trim();
            LastError = text.Length == 0 ? $"nvidia-smi endete mit Exitcode {r.ExitCode}" : text;
            if (!SkipInvalidField(text)) break;
        }
        return Array.Empty<GpuSample>();
    }

    /// <summary>Ersetzt das von nvidia-smi als ungueltig gemeldete Feld. False, wenn der Fehler etwas anderes war.</summary>
    private static bool SkipInvalidField(string errorText)
    {
        var m = InvalidField.Match(errorText);
        if (!m.Success) return false;

        lock (FieldLock)
        {
            for (var i = 0; i < Fields.Length; i++)
            {
                if (Chosen[i] >= Fields[i].Length ||
                    !Fields[i][Chosen[i]].Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) continue;

                Log.Warn($"nvidia-smi kennt das Feld '{Fields[i][Chosen[i]]}' nicht" +
                         (Chosen[i] + 1 < Fields[i].Length ? $", weiche auf '{Fields[i][Chosen[i] + 1]}' aus." : ", das Feld entfaellt."));
                Chosen[i]++;
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<GpuSample> Parse(string stdOut, int[] active)
    {
        var samples = new List<GpuSample>();
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < active.Length) continue;

            // Wert je Feld ueber den Feldindex; entfallene Felder bleiben null.
            var v = new string?[Fields.Length];
            for (var k = 0; k < active.Length; k++) v[active[k]] = parts[k];

            samples.Add(new GpuSample(
                Name: v[IName] ?? "",
                DriverVersion: v[IDriver] ?? "",
                TemperatureC: Num(v[ITemp]),
                HotspotC: null,
                UtilizationPercent: Num(v[IUtil]),
                PowerWatt: Num(v[IPower]),
                PowerLimitWatt: Num(v[IPowerLimit]),
                ClockMhz: Num(v[IClock]),
                MemoryUsedMb: Num(v[IMemUsed]),
                MemoryTotalMb: Num(v[IMemTotal]),
                PerformanceState: string.IsNullOrWhiteSpace(v[IPState]) ? null : v[IPState],
                FanPercent: Num(v[IFan]),
                MemoryUtilPercent: Num(v[IMemUtil]),
                EncoderUtilPercent: Num(v[IEnc]),
                DecoderUtilPercent: Num(v[IDec]),
                PcieLinkGenCurrent: (int?)Num(v[IGenCur]),
                PcieLinkGenMax: (int?)Num(v[IGenMax]),
                PcieLinkWidthCurrent: (int?)Num(v[IWidthCur]),
                PcieLinkWidthMax: (int?)Num(v[IWidthMax]),
                ThrottlePowerCap: Bool(v[IThrPower]),
                ThrottleThermal: Bool(v[IThrThermal]),
                ThrottleHwSlowdown: Bool(v[IThrHw])));
        }
        return samples;
    }

    /// <summary>
    /// Liest, welche Prozesse gerade Grafikspeicher belegen (VRAM-genau ueber den
    /// Treiber). Anders als die Prozent-Spalte im Task-Manager - die nur die
    /// 3D-Engine eines einzelnen Prozesses misst und Arbeit auf anderen Engines
    /// (Video Encode/Decode, Copy) oder auf mehrere Prozesse verteilte Last
    /// systematisch unterschaetzt - ist die VRAM-Belegung hier direkt vom Treiber.
    /// </summary>
    public static async Task<IReadOnlyList<GpuProcessSample>> SampleProcessesAsync(CancellationToken ct = default)
    {
        if (Path is null) return Array.Empty<GpuProcessSample>();

        var r = await Shell.RunAsync(Path,
            "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits", 10_000, ct);
        if (!r.Success) return Array.Empty<GpuProcessSample>();

        var samples = new List<GpuProcessSample>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out var pid)) continue;
            var mem = Num(parts[2]);
            if (mem is null) continue;

            samples.Add(new GpuProcessSample(pid, parts[1], mem.Value));
        }
        return samples.OrderByDescending(p => p.UsedMemoryMb).ToList();
    }

    /// <summary>Vollstaendiger nvidia-smi-Bericht (fuer den Diagnosebericht).</summary>
    public static async Task<string> FullReportAsync(CancellationToken ct = default)
    {
        if (Path is null) return "nvidia-smi nicht gefunden (keine NVIDIA-Grafikkarte oder Treiber unvollstaendig).";
        var r = await Shell.RunAsync(Path, "-q", 20_000, ct);
        return r.Success ? r.StdOut : r.Combined;
    }

    private static double? Num(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? Bool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("N/A", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Not Supported", StringComparison.OrdinalIgnoreCase)) return null;
        return s.Trim().Equals("Active", StringComparison.OrdinalIgnoreCase);
    }
}
