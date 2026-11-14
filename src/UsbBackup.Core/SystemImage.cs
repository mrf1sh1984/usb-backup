using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbBackup.Core;

public enum SystemImageOutcome { Succeeded, Failed, ElevationDeclined }

public sealed record SystemImageResult(SystemImageOutcome Outcome, int? ExitCode, string LogFile);

/// <summary>
/// A full, bootable-restore image of the Windows drive made with Windows' own imaging tool
/// (wbadmin). It is written to WindowsImageBackup on the destination and is restored from
/// Windows Recovery ("System Image Recovery"), not from this app.
/// </summary>
public static class SystemImage
{
    private static readonly Regex Percent = new(@"\((\d{1,3})%\)", RegexOptions.Compiled);

    public static string SystemDrive => (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');

    public static string BuildArguments(string targetLetter) =>
        $"start backup -backupTarget:{targetLetter} -include:{SystemDrive} -allCritical -quiet";

    /// <summary>Pulls the percentage out of a wbadmin progress line, if it has one.</summary>
    public static int? ParsePercent(string line)
    {
        var m = Percent.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p <= 100 ? p : null;
    }

    /// <summary>
    /// Runs wbadmin with administrator rights (Windows asks for consent) and streams its output.
    /// </summary>
    public static async Task<SystemImageResult> RunAsync(string targetLetter, IProgress<string> output, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(targetLetter, "^[A-Za-z]:$")) throw new ArgumentException("Expected a drive letter such as J:", nameof(targetLetter));

        var log = Path.Combine(Path.GetTempPath(), $"UsbBackup-image-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var wbadmin = Path.Combine(Environment.SystemDirectory, "wbadmin.exe");
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c \"\"{wbadmin}\" {BuildArguments(targetLetter)} > \"{log}\" 2>&1\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the user said no at the consent prompt
        {
            return new SystemImageResult(SystemImageOutcome.ElevationDeclined, null, log);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (Exception) { encoding = Encoding.UTF8; }

        long position = 0;
        var pending = new StringBuilder();
        var sawSuccess = false;

        void Drain()
        {
            if (!File.Exists(log)) return;
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= position) return;
            fs.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - position];
            var read = fs.Read(buffer, 0, buffer.Length);
            position += read;
            pending.Append(encoding.GetString(buffer, 0, read));
            int nl;
            while ((nl = pending.ToString().IndexOfAny(new[] { '\r', '\n' })) >= 0)
            {
                var line = pending.ToString(0, nl).Trim();
                pending.Remove(0, nl + 1);
                if (line.Length == 0) continue;
                if (line.Contains("successfully completed", StringComparison.OrdinalIgnoreCase)) sawSuccess = true;
                output.Report(line);
            }
        }

        using (process)
        {
            while (!process.HasExited)
            {
                try { Drain(); } catch (IOException) { }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            try { Drain(); } catch (IOException) { }

            int? exit = null;
            try { exit = process.ExitCode; } catch (Exception) { /* not readable across the elevation boundary on some systems */ }
            var ok = exit is int code ? code == 0 : sawSuccess;
            return new SystemImageResult(ok ? SystemImageOutcome.Succeeded : SystemImageOutcome.Failed, exit, log);
        }
    }

    /// <summary>Asks Windows to stop the image that is currently being written (needs consent again).</summary>
    public static void RequestStop()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "wbadmin.exe"),
                Arguments = "stop job -quiet",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Win32Exception) { /* consent declined: the image carries on */ }
    }
}
