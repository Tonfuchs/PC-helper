using System.Management;
using PCHelper.Core;

namespace PCHelper.Diagnostics;

/// <summary>Bequemer, ausfallsicherer Zugriff auf WMI/CIM.</summary>
public static class Wmi
{
    /// <summary>
    /// Fuehrt eine WQL-Abfrage aus und liefert die Ergebnisse als Dictionarys.
    /// Bei Fehlern (WMI defekt, Rechte, Timeout) kommt eine leere Liste zurueck.
    /// </summary>
    public static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2")
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            using var results = searcher.Get();

            foreach (var o in results)
            {
                using var mo = (ManagementObject)o;
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in mo.Properties)
                {
                    try { dict[p.Name] = p.Value; } catch { dict[p.Name] = null; }
                }
                rows.Add(dict);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI-Abfrage fehlgeschlagen ({scope}: {wql}): {ex.Message}");
        }
        return rows;
    }

    public static string Str(this Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString()?.Trim() ?? "" : "";

    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    public static int? Int(this Dictionary<string, object?> row, string key)
    {
        var l = row.Long(key);
        return l is null ? null : (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue);
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToBoolean(v); } catch { return null; }
    }

    /// <summary>Wandelt ein WMI-DATETIME ("20260131120000.000000+060") in DateTime.</summary>
    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
