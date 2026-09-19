using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PCHelper.Core;

/// <summary>Persistente Benutzereinstellungen (%LOCALAPPDATA%\PCHelper\settings.json).</summary>
public sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PCHelper";

    public string RepoOwner { get; set; } = AppInfo.DefaultRepoOwner;
    public string RepoName { get; set; } = AppInfo.DefaultRepoName;

    /// <summary>Beim Start automatisch nach Updates suchen.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Wissensdatenbank beim Start aus dem Repo nachladen.</summary>
    public bool AutoRefreshKnowledgeBase { get; set; } = true;

    /// <summary>Ueberwachung automatisch starten, sobald die App laeuft.</summary>
    public bool AutoStartMonitoring { get; set; } = true;

    /// <summary>Abtastintervall der Ueberwachung in Sekunden.</summary>
    public int SampleIntervalSeconds { get; set; } = 5;

    /// <summary>Messreihen aelter als X Tage werden geloescht.</summary>
    public int TelemetryRetentionDays { get; set; } = 30;

    /// <summary>Beim Schliessen in den Infobereich minimieren statt beenden.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Fenster beim automatischen Start (Autostart) verbergen.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Schluessel der Wartungspunkte, die als bewusste Entscheidung markiert wurden ("Ist Absicht").
    /// Sie zaehlen nicht mehr als Problem und stehen unten in der Liste.
    /// </summary>
    public List<string> IntentionalItems { get; set; } = new();

    /// <summary>
    /// Dienste, die ueber die Wartung auf "Manuell" gestellt wurden. Nur so laesst sich die Aenderung
    /// spaeter wieder anbieten: ein Dienst auf "Manuell" taucht sonst in keiner Liste mehr auf.
    /// </summary>
    public List<string> ManualizedServices { get; set; } = new();

    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// Startzeitpunkt von Windows, fuer den bereits gefragt wurde, ob der
    /// Neustart wegen eines Schwarzbildes noetig war. Verhindert Nachfragen.
    /// </summary>
    public DateTime? LastRestartPromptBootTime { get; set; }

    [JsonIgnore]
    public string RawIssuesUrl =>
        $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/main/knowledge/known-issues.json";

    [JsonIgnore]
    public string ReleasesApiUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    [JsonIgnore]
    public string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

    // ---------- Autostart (HKCU, kein Adminrecht noetig) ----------

    /// <summary>Startet die App mit Windows? Liest/schreibt den HKCU-Run-Schluessel.</summary>
    [JsonIgnore]
    public bool StartWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValueName) is string s && s.Contains("PCHelper", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key is null) return;
                if (value)
                    key.SetValue(RunValueName, $"\"{AppInfo.ExecutablePath}\" --autostart");
                else if (key.GetValue(RunValueName) is not null)
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Error("Autostart konnte nicht gesetzt werden", ex);
            }
        }
    }

    // ---------- Laden / Speichern ----------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppInfo.SettingsFile))
            {
                var json = File.ReadAllText(AppInfo.SettingsFile);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null) return loaded.Sanitized();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht geladen werden - es gelten die Standardwerte", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppInfo.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
        }
    }

    private Settings Sanitized()
    {
        if (string.IsNullOrWhiteSpace(RepoOwner)) RepoOwner = AppInfo.DefaultRepoOwner;
        if (string.IsNullOrWhiteSpace(RepoName)) RepoName = AppInfo.DefaultRepoName;
        SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, 2, 120);
        TelemetryRetentionDays = Math.Clamp(TelemetryRetentionDays, 1, 365);
        return this;
    }
}
