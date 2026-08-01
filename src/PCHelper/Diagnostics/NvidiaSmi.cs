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
    string? PerformanceState);

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
        "clocks.current.graphics,memory.used,memory.total,pstate";

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
                PerformanceState: string.IsNullOrWhiteSpace(parts[9]) ? null : parts[9]));
        }
        return samples;
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
}
