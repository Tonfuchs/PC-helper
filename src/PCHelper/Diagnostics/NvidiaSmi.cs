using System.Globalization;
using System.IO;
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

    private const string QueryFields =
        "name,driver_version,temperature.gpu,utilization.gpu,power.draw,power.limit," +
        "clocks.current.graphics,memory.used,memory.total,pstate," +
        "fan.speed,utilization.memory,utilization.encoder,utilization.decoder," +
        "pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max," +
        "clocks_throttle_reasons.sw_power_cap,clocks_throttle_reasons.thermal," +
        "clocks_throttle_reasons.hw_slowdown";

    /// <summary>Liest die aktuellen Werte aller NVIDIA-GPUs. Leere Liste, wenn nicht verfuegbar.</summary>
    public static async Task<IReadOnlyList<GpuSample>> SampleAsync(CancellationToken ct = default)
    {
        if (Path is null) return Array.Empty<GpuSample>();

        var r = await Shell.RunAsync(Path, $"--query-gpu={QueryFields} --format=csv,noheader,nounits", 10_000, ct);
        if (!r.Success) return Array.Empty<GpuSample>();

        var samples = new List<GpuSample>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 10) continue;

            samples.Add(new GpuSample(
                Name: parts[0],
                DriverVersion: parts[1],
                TemperatureC: Num(parts[2]),
                HotspotC: null,
                UtilizationPercent: Num(parts[3]),
                PowerWatt: Num(parts[4]),
                PowerLimitWatt: Num(parts[5]),
                ClockMhz: Num(parts[6]),
                MemoryUsedMb: Num(parts[7]),
                MemoryTotalMb: Num(parts[8]),
                PerformanceState: string.IsNullOrWhiteSpace(parts[9]) ? null : parts[9],
                FanPercent: parts.Length > 10 ? Num(parts[10]) : null,
                MemoryUtilPercent: parts.Length > 11 ? Num(parts[11]) : null,
                EncoderUtilPercent: parts.Length > 12 ? Num(parts[12]) : null,
                DecoderUtilPercent: parts.Length > 13 ? Num(parts[13]) : null,
                PcieLinkGenCurrent: parts.Length > 14 ? (int?)Num(parts[14]) : null,
                PcieLinkGenMax: parts.Length > 15 ? (int?)Num(parts[15]) : null,
                PcieLinkWidthCurrent: parts.Length > 16 ? (int?)Num(parts[16]) : null,
                PcieLinkWidthMax: parts.Length > 17 ? (int?)Num(parts[17]) : null,
                ThrottlePowerCap: parts.Length > 18 ? Bool(parts[18]) : null,
                ThrottleThermal: parts.Length > 19 ? Bool(parts[19]) : null,
                ThrottleHwSlowdown: parts.Length > 20 ? Bool(parts[20]) : null));
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

    private static double? Num(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? Bool(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return s.Trim().Equals("Active", StringComparison.OrdinalIgnoreCase);
    }
}
