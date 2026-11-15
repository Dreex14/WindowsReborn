using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsReborn.Programs;

public sealed record WinGetPackage(string Name, string Id, string Version);

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Abstracción para poder simular WinGet en las pruebas.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunHiddenAsync(string exe, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>Ejecuta en una ventana visible (el usuario ve y responde a las preguntas).</summary>
    Task<int> RunVisibleAsync(string exe, string arguments, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunHiddenAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process could not be started.");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new ProcessResult(p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    public async Task<int> RunVisibleAsync(string exe, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process could not be started.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }
}

/// <summary>
/// Integración con el cliente oficial <c>winget</c>. Solo se usa la fuente
/// comunitaria <c>winget</c> (sin acuerdos que aceptar en segundo plano). Las
/// instalaciones se lanzan en una consola visible para que el usuario vea y acepte
/// los acuerdos de cada paquete.
/// </summary>
public sealed class WinGetClient(IProcessRunner runner)
{
    public const string Exe = "winget";

    /// <summary>Código de WinGet «no se encontró ningún paquete instalado».</summary>
    public const int NoPackagesFound = unchecked((int)0x8A150014);

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var r = await runner.RunHiddenAsync(Exe, ["--version"], ct).ConfigureAwait(false);
            return r.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<WinGetPackage>> SearchAsync(string query, CancellationToken ct)
    {
        var r = await runner.RunHiddenAsync(Exe, ["search", "--name", query, "--source", "winget", "--disable-interactivity"], ct).ConfigureAwait(false);
        if (r.ExitCode == NoPackagesFound) return [];
        if (r.ExitCode != 0) throw new WinGetException(r.ExitCode, r.Output);
        return ParseTable(r.Output);
    }

    /// <summary>Última versión disponible de un Id exacto, o null si no existe.</summary>
    public async Task<string?> GetLatestVersionAsync(string id, CancellationToken ct)
    {
        var r = await runner.RunHiddenAsync(Exe, ["search", "--id", id, "--exact", "--source", "winget", "--disable-interactivity"], ct).ConfigureAwait(false);
        if (r.ExitCode == NoPackagesFound) return null;
        if (r.ExitCode != 0) throw new WinGetException(r.ExitCode, r.Output);
        return ParseTable(r.Output).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Version;
    }

    public async Task<bool?> IsInstalledAsync(string id, CancellationToken ct)
    {
        var r = await runner.RunHiddenAsync(Exe, ["list", "--id", id, "--exact", "--disable-interactivity"], ct).ConfigureAwait(false);
        if (r.ExitCode == 0) return true;
        if (r.ExitCode == NoPackagesFound) return false;
        return null;
    }

    /// <summary>
    /// Descarga el instalador con <c>winget download</c>, que comprueba el SHA-256 del
    /// manifiesto. Devuelve la ruta del instalador y la versión.
    /// </summary>
    public async Task<(string File, string? Version)> DownloadAsync(string id, string architecture, string installerType,
        string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var args = new List<string> { "download", "--id", id, "--exact", "--source", "winget", "--download-directory", directory, "--disable-interactivity" };
        if (architecture is "x64" or "x86" or "arm64")
        {
            args.Add("--architecture");
            args.Add(architecture);
        }
        if (installerType is "exe" or "msi" or "msix")
        {
            args.Add("--installer-type");
            args.Add(installerType);
        }

        var before = Directory.GetFiles(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var r = await runner.RunHiddenAsync(Exe, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new WinGetException(r.ExitCode, r.Output);

        var created = Directory.GetFiles(directory).Where(f => !before.Contains(f)).ToList();
        var installer = created.FirstOrDefault(f => !f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new WinGetException(r.ExitCode, "No installer file was produced.");
        string? version = null;
        var yaml = created.FirstOrDefault(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
        if (yaml is not null)
        {
            var m = Regex.Match(await File.ReadAllTextAsync(yaml, ct).ConfigureAwait(false), @"^PackageVersion:\s*['""]?([^'""\r\n]+)", RegexOptions.Multiline);
            if (m.Success) version = m.Groups[1].Value.Trim();
            try { File.Delete(yaml); } catch { }
        }
        return (installer, version);
    }

    /// <summary>Construye los argumentos de instalación; se ejecuta en una consola visible.</summary>
    public static string InstallArguments(ProgramEntry entry, bool silent)
    {
        var sb = new StringBuilder($"install --id {Quote(entry.WinGetId!)} --exact --source winget");
        if (entry.Architecture is "x64" or "x86" or "arm64")
            sb.Append(" --architecture ").Append(entry.Architecture);
        sb.Append(silent ? " --silent" : " --interactive");
        return sb.ToString();
    }

    public Task<int> InstallAsync(ProgramEntry entry, bool silent, CancellationToken ct) =>
        runner.RunVisibleAsync(Exe, InstallArguments(entry, silent), ct);

    public async Task ExportAsync(string outputFile, CancellationToken ct)
    {
        var r = await runner.RunHiddenAsync(Exe, ["export", "-o", outputFile, "--source", "winget", "--include-versions", "--disable-interactivity"], ct).ConfigureAwait(false);
        // WinGet devuelve error si algunos programas no se pudieron emparejar, pero el archivo se crea igualmente.
        if (!File.Exists(outputFile)) throw new WinGetException(r.ExitCode, r.Output);
    }

    /// <summary>
    /// Interpreta la tabla de <c>winget search</c>. Las columnas se localizan por las
    /// posiciones de la cabecera (sus nombres pueden estar traducidos; el orden no).
    /// </summary>
    internal static IReadOnlyList<WinGetPackage> ParseTable(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();
        var sep = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        if (sep <= 0) return [];

        var header = lines[sep - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        }
        if (starts.Count < 3) return [];

        var result = new List<WinGetPackage>();
        foreach (var line in lines.Skip(sep + 1))
        {
            if (line.Length <= starts[2]) continue;
            string Col(int idx)
            {
                var start = starts[idx];
                var end = idx + 1 < starts.Count ? Math.Min(starts[idx + 1], line.Length) : line.Length;
                return start >= line.Length ? "" : line[start..end].Trim();
            }
            var id = Col(1);
            if (id.Length == 0 || id.Contains(' ') || id.EndsWith('…')) continue;
            result.Add(new WinGetPackage(Col(0), id, Col(2)));
        }
        return result;
    }

    private static string Quote(string s) => s.Any(char.IsWhiteSpace) || s.Contains('"') ? "\"" + s.Replace("\"", "") + "\"" : s;
}

public sealed class WinGetException(int exitCode, string output) : Exception($"winget exited with code 0x{exitCode:X8}")
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}
