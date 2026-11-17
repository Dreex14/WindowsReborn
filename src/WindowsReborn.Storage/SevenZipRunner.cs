using System.Diagnostics;
using System.Text;

namespace WindowsReborn.Storage;

/// <summary>
/// Ejecuta el <c>7z.exe</c> instalado en el equipo. Windows Reborn no incluye ni
/// descarga 7-Zip: si no está instalado, el formato 7z simplemente no se ofrece.
/// </summary>
public static class SevenZipRunner
{
    public static string? Locate(string? userPath)
    {
        if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            return userPath;

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public sealed record Result(int ExitCode, string StdErr);

    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string workingDirectory,
        IReadOnlyList<string>? stdinLines, Action<string>? status, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-sccUTF-8");
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new IOException("7-Zip could not be started.");
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data?.Trim();
            if (!string.IsNullOrEmpty(line) && line.Contains('%'))
                status?.Invoke(line);
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        if (stdinLines is not null)
        {
            foreach (var line in stdinLines)
                await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new Result(process.ExitCode, stderr.ToString());
    }
}
