using System.IO;
using System.Text;

namespace PCHelper.Core;

/// <summary>
/// Fuehrt kurze PowerShell-Skripte aus. Skripte werden als Base64 uebergeben (kein Quoting-Aerger)
/// bzw. fuer die Elevation in eine Datei geschrieben und ueber die Batch-Huelle der Reparaturen gestartet.
/// </summary>
public static class PowerShellRunner
{
    /// <summary>Kennzeichen, mit dem ein Skript sein Ergebnis in die Ausgabe schreibt.</summary>
    public const string ResultMarker = "PCH-RESULT:";

    private const string Prelude =
        "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8;\n";

    /// <summary>Setzt einen Wert als PowerShell-String in einfachen Anfuehrungszeichen ein.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Laeuft ohne Fenster und ohne Elevation. Fuer reine Abfragen.</summary>
    public static Task<ProcessResult> RunAsync(string script, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script));
        return Shell.RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs, ct);
    }

    /// <summary>
    /// Fuehrt ein Skript mit Administratorrechten aus (eine UAC-Abfrage). Das Skript meldet sein Ergebnis mit
    /// <c>Write-Output 'PCH-RESULT:OK|Text'</c> oder <c>'PCH-RESULT:FAIL|Text'</c>.
    /// </summary>
    public static Task<(bool Success, string Message)> RunElevatedAsync(string script, string label)
        => RunScriptAsync(script, label, elevated: true);

    /// <summary>Wie <see cref="RunElevatedAsync"/>, wahlweise ohne Elevation (fuer den Selbsttest des Skriptwegs).</summary>
    public static async Task<(bool Success, string Message)> RunScriptAsync(string script, string label, bool elevated)
    {
        var dir = Path.Combine(AppInfo.DataDir, "fixes");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}.ps1");

        // Mit Byte-Order-Mark, damit Windows PowerShell 5.1 auch Umlaute in Namen richtig liest.
        await File.WriteAllTextAsync(path, Prelude + script, new UTF8Encoding(true));

        var result = await Shell.RunBatchAsync(
            new[] { $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{path}\"" }, label, elevated);

        return ParseResult(result);
    }

    /// <summary>
    /// Prueft die Syntax eines Skripts, ohne es auszufuehren. Gibt null zurueck, wenn alles in Ordnung ist,
    /// sonst die Fehlermeldungen. Skripte, die Administratorrechte brauchen, lassen sich so ohne UAC-Abfrage testen.
    /// </summary>
    public static async Task<string?> CheckSyntaxAsync(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pchelper-syntax-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(path, Prelude + script, new UTF8Encoding(true));
            var checker =
                "$t = $null; $e = $null\n" +
                $"[System.Management.Automation.Language.Parser]::ParseFile({Quote(path)}, [ref]$t, [ref]$e) | Out-Null\n" +
                "foreach ($x in $e) { Write-Output ('Zeile ' + $x.Extent.StartLineNumber + ': ' + $x.Message) }";

            var result = await RunAsync(checker, 30_000);
            var errors = result.StdOut.Trim();
            return errors.Length == 0 ? null : errors;
        }
        finally
        {
            try { File.Delete(path); } catch { /* Temp-Datei: egal */ }
        }
    }

    /// <summary>Liest die Ergebniszeile eines Skripts aus der Ausgabe.</summary>
    public static (bool Success, string Message) ParseResult(ProcessResult result)
    {
        if (result.ExitCode == 1223)
            return (false, "Die Administrator-Abfrage wurde abgebrochen - es wurde nichts geaendert.");

        foreach (var raw in result.Combined.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(ResultMarker, StringComparison.Ordinal);
            if (at < 0 || line.StartsWith("echo", StringComparison.OrdinalIgnoreCase) || line.StartsWith('>')) continue;

            var payload = line[(at + ResultMarker.Length)..];
            var bar = payload.IndexOf('|');
            var state = bar < 0 ? payload : payload[..bar];
            var text = bar < 0 ? "" : payload[(bar + 1)..];
            return (state.Equals("OK", StringComparison.OrdinalIgnoreCase), text);
        }

        var tail = string.Join(" ", result.Combined.Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('>') && !l.StartsWith("===")).TakeLast(3));
        return (false, "Das Skript hat kein Ergebnis gemeldet. " + tail);
    }
}
