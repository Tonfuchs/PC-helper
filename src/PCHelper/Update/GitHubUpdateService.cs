using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCHelper.Core;

namespace PCHelper.Update;

/// <summary>Beschreibt eine im Repo verfuegbare neuere Fassung.</summary>
public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string Title { get; init; }
    public required string Notes { get; init; }
    public required string DownloadUrl { get; init; }
    public string? ChecksumUrl { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? PublishedAt { get; init; }
    public required string ReleasePageUrl { get; init; }

    public string SizeText => SizeBytes <= 0 ? "" : $"{SizeBytes / 1024.0 / 1024.0:0.#} MB";
}

/// <summary>
/// Update ueber GitHub Releases.
///
/// Ablauf: Version des neuesten Releases abfragen, bei Bedarf die EXE
/// herunterladen, gegen SHA256SUMS.txt pruefen und ueber ein kleines
/// Hilfsskript austauschen, sobald sich das Programm beendet hat.
/// </summary>
public sealed class GitHubUpdateService
{
    private readonly Settings _settings;

    public GitHubUpdateService(Settings settings) => _settings = settings;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PCHelper/{AppInfo.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Fragt das neueste Release ab. Liefert null, wenn bereits die aktuelle
    /// Fassung laeuft oder die Abfrage fehlschlaegt (z. B. ohne Internet).
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient();
            using var response = await http.GetAsync(_settings.ReleasesApiUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Update-Abfrage: HTTP {(int)response.StatusCode} von {_settings.ReleasesApiUrl}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version is null)
            {
                Log.Warn($"Update-Abfrage: Tag '{tag}' ist keine Versionsnummer.");
                return null;
            }

            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();

            if (version <= AppInfo.Version) return null;

            string? downloadUrl = null, checksumUrl = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString();

                    if (name.Equals(AppInfo.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = url;
                        size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    }
                    else if (name.Equals(AppInfo.ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        checksumUrl = url;
                    }
                }
            }

            if (downloadUrl is null)
            {
                Log.Warn($"Release {tag} enthaelt kein Asset namens '{AppInfo.ReleaseAssetName}'.");
                return null;
            }

            return new UpdateInfo
            {
                Version = version,
                Tag = tag,
                Title = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                DownloadUrl = downloadUrl,
                ChecksumUrl = checksumUrl,
                SizeBytes = size,
                PublishedAt = root.TryGetProperty("published_at", out var pa) && pa.TryGetDateTime(out var dt) ? dt.ToLocalTime() : null,
                ReleasePageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? _settings.RepoUrl : _settings.RepoUrl,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Update-Abfrage fehlgeschlagen: " + ex.Message);
            return null;
        }
    }

    /// <summary>Laedt die neue Fassung herunter und prueft, sofern moeglich, die Pruefsumme.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var target = Path.Combine(AppInfo.UpdateDir, $"PCHelper-{info.Version}.exe");
        if (File.Exists(target)) File.Delete(target);

        using var http = CreateClient();

        using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.SizeBytes;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, count), ct);
                read += count;
                if (total > 0) progress?.Report(Math.Round(read * 100.0 / total, 1));
            }
        }

        if (new FileInfo(target).Length < 1024 * 512)
            throw new InvalidOperationException("Die heruntergeladene Datei ist unplausibel klein - Download abgebrochen.");

        if (info.ChecksumUrl is not null)
            await VerifyChecksumAsync(http, info.ChecksumUrl, target, ct);
        else
            Log.Warn("Kein SHA256SUMS.txt im Release - die Pruefsumme konnte nicht verifiziert werden.");

        Log.Info($"Update {info.Version} heruntergeladen: {target}");
        return target;
    }

    private static async Task VerifyChecksumAsync(HttpClient http, string checksumUrl, string file, CancellationToken ct)
    {
        string content;
        try { content = await http.GetStringAsync(checksumUrl, ct); }
        catch (Exception ex)
        {
            Log.Warn("Pruefsummendatei nicht abrufbar: " + ex.Message);
            return;
        }

        var expected = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(AppInfo.ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            ?.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Length == 64);

        if (expected is null)
        {
            Log.Warn("In SHA256SUMS.txt wurde kein passender Eintrag gefunden.");
            return;
        }

        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidOperationException(
                "Die Pruefsumme der heruntergeladenen Datei stimmt nicht. Das Update wurde verworfen.");
        }

        Log.Info("Pruefsumme des Updates verifiziert.");
    }

    /// <summary>
    /// Tauscht die laufende EXE gegen die heruntergeladene aus und startet neu.
    /// Das Hilfsskript wartet, bis sich dieser Prozess beendet hat.
    /// </summary>
    public static void ApplyAndRestart(string downloadedExe)
    {
        var current = AppInfo.ExecutablePath;
        var script = Path.Combine(AppInfo.UpdateDir, "apply-update.cmd");
        var pid = Environment.ProcessId;

        var cmd = $"""
            @echo off
            setlocal
            rem Warten, bis sich PC Helper (PID {pid}) beendet hat.
            set /a tries=0
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if errorlevel 1 goto ready
            set /a tries+=1
            if %tries% GTR 60 goto ready
            ping -n 2 127.0.0.1 >nul
            goto wait

            :ready
            ping -n 2 127.0.0.1 >nul
            copy /y "{downloadedExe}" "{current}" >nul
            if errorlevel 1 (
              echo Das Update konnte nicht angewendet werden.
              echo Die neue Fassung liegt hier: {downloadedExe}
              pause
              exit /b 1
            )
            del /q "{downloadedExe}" >nul 2>&1
            start "" "{current}"
            exit /b 0
            """;

        File.WriteAllText(script, cmd, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        Log.Info("Update wird angewendet, Programm wird beendet.");
    }

    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(cleaned, out var v)
            ? new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0))
            : null;
    }
}
