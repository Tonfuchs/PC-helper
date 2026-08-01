using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PCHelper.Core;

/// <summary>
/// Zentrale Konstanten, Pfade und Versionsinformationen.
/// </summary>
public static class AppInfo
{
    /// <summary>Anzeigename der Anwendung.</summary>
    public const string Name = "PC Helper";

    /// <summary>
    /// GitHub-Repository fuer Updates und Wissensdatenbank.
    /// WICHTIG: Beim Fork bzw. eigener Veroeffentlichung hier anpassen
    /// (oder zur Laufzeit unter "Einstellungen" ueberschreiben).
    /// </summary>
    public const string DefaultRepoOwner = "Tonfuchs";

    /// <inheritdoc cref="DefaultRepoOwner"/>
    public const string DefaultRepoName = "PC-helper";

    /// <summary>Dateiname des Release-Assets, das die Update-Funktion herunterlaedt.</summary>
    public const string ReleaseAssetName = "PCHelper.exe";

    /// <summary>Name der Pruefsummen-Datei im Release (optional, wird verifiziert wenn vorhanden).</summary>
    public const string ChecksumAssetName = "SHA256SUMS.txt";

    private static readonly Lazy<Version> _version = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        // InformationalVersion kann "1.2.3+sha" enthalten -> abschneiden.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var clean = info.Split('+', '-')[0];
            if (Version.TryParse(clean, out var parsed))
                return Normalize(parsed);
        }
        return Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
    });

    /// <summary>Aktuelle Programmversion (dreistellig normalisiert).</summary>
    public static Version Version => _version.Value;

    /// <summary>Version als Anzeigetext, z. B. "v1.4.2".</summary>
    public static string VersionDisplay => "v" + Version.ToString(3);

    /// <summary>
    /// Vollstaendiger Pfad der laufenden EXE.
    /// Assembly.Location scheidet aus: bei einer Single-File-EXE ist es leer.
    /// </summary>
    public static string ExecutablePath
        => Environment.ProcessPath
           ?? Process.GetCurrentProcess().MainModule?.FileName
           ?? Path.Combine(AppContext.BaseDirectory, "PCHelper.exe");

    /// <summary>Datenverzeichnis: %LOCALAPPDATA%\PCHelper</summary>
    public static string DataDir { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCHelper"));

    /// <summary>Ablage der Messreihen (JSONL, taeglich rotierend).</summary>
    public static string TelemetryDir { get; } = EnsureDir(Path.Combine(DataDir, "telemetry"));

    /// <summary>Ablage erkannter/gemeldeter Vorfaelle.</summary>
    public static string IncidentDir { get; } = EnsureDir(Path.Combine(DataDir, "incidents"));

    /// <summary>Temporaeres Verzeichnis fuer heruntergeladene Updates.</summary>
    public static string UpdateDir { get; } = EnsureDir(Path.Combine(DataDir, "updates"));

    /// <summary>Berichte landen sichtbar unter Dokumente\PC Helper\Berichte.</summary>
    public static string ReportDir { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Helper", "Berichte"));

    /// <summary>Pfad der Einstellungsdatei.</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");

    /// <summary>Pfad der Programm-Logdatei.</summary>
    public static string LogFile { get; } = Path.Combine(DataDir, "pchelper.log");

    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* Verzeichnis nicht anlegbar -> Aufrufer faengt IO-Fehler ab */ }
        return path;
    }
}
