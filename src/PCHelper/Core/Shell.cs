using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace PCHelper.Core;

/// <summary>Ergebnis eines externen Prozessaufrufs.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

/// <summary>Hilfsfunktionen zum Ausfuehren externer Programme, Elevation und Link-Oeffnen.</summary>
public static class Shell
{
    /// <summary>Laeuft der Prozess mit Administratorrechten?</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Fuehrt ein Programm ohne Fenster aus und liefert Ausgabe + Exitcode.
    /// Wirft nicht; Fehler werden als ExitCode -1 zurueckgegeben.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName, string arguments, int timeoutMs = 30_000, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new ProcessResult(-1, "", "Prozess konnte nicht gestartet werden.");

            var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
                return new ProcessResult(-1, "", $"Zeitueberschreitung nach {timeoutMs} ms: {fileName}");
            }

            return new ProcessResult(proc.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Fuehrt mehrere Kommandozeilen gebuendelt als Batchdatei mit Adminrechten aus (eine UAC-Abfrage).
    /// Liefert die Protokollausgabe der Batchdatei zurueck.
    /// </summary>
    public static async Task<ProcessResult> RunElevatedBatchAsync(IEnumerable<string> commands, string label)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(AppInfo.DataDir, "fixes");
        Directory.CreateDirectory(dir);
        var cmdPath = Path.Combine(dir, $"{stamp}-{Sanitize(label)}.cmd");
        var logPath = Path.Combine(dir, $"{stamp}-{Sanitize(label)}.log");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"echo === {label} ===");
        foreach (var c in commands)
        {
            sb.AppendLine($"echo.");
            sb.AppendLine($"echo ^> {c.Replace("^", "^^").Replace("&", "^&").Replace("<", "^<").Replace(">", "^>").Replace("|", "^|")}");
            sb.AppendLine(c);
            sb.AppendLine("if errorlevel 1 echo [FEHLER] Exitcode %errorlevel%");
        }
        sb.AppendLine("echo.");
        sb.AppendLine("echo === Fertig ===");
        sb.AppendLine("exit /b 0");

        await File.WriteAllTextAsync(cmdPath, sb.ToString(), new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{cmdPath}\" > \"{logPath}\" 2>&1\"")
            {
                UseShellExecute = true,   // fuer Verb=runas zwingend
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new ProcessResult(-1, "", "Der Vorgang wurde nicht gestartet.");

            await proc.WaitForExitAsync();

            var output = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "";
            return new ProcessResult(proc.ExitCode, output, "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ProcessResult(1223, "", "Die Administrator-Abfrage wurde abgebrochen - es wurde nichts geaendert.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    /// <summary>Oeffnet eine URL, Datei oder einen Ordner mit der Standardanwendung.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Konnte '{target}' nicht oeffnen", ex);
        }
    }

    /// <summary>Startet ein Windows-Werkzeug (z. B. msinfo32) und meldet Fehler nur ins Log.</summary>
    public static void Launch(string fileName, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Konnte '{fileName}' nicht starten", ex);
        }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "fix" : new string(chars);
    }
}
