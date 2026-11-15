using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Logging;

namespace WindowsReborn.Tweaks;

public sealed class ElevatedRequest
{
    public List<RegOp> Ops { get; set; } = [];
    public bool CreateRestorePoint { get; set; }
}

public sealed class ElevatedResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? RestorePointCreated { get; set; }
}

public sealed class ElevationCancelledException() : Exception("The administrator permission request was cancelled.");

public sealed class RestorePointException(string message) : Exception(message);

/// <summary>
/// Escritura real. HKCU se escribe en este proceso; HKLM y los puntos de restauración
/// se envían a una copia elevada de la aplicación (una sola petición de UAC por lote),
/// que solo acepta operaciones del catálogo.
/// </summary>
public sealed class SystemRegistryWriter(string stateDirectory) : IRegistryWriter
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task WriteAsync(IReadOnlyList<RegOp> ops, bool createRestorePoint, CancellationToken ct = default)
    {
        var machine = ops.Where(o => o.Hive == RegHive.LocalMachine).ToList();
        var user = ops.Where(o => o.Hive == RegHive.CurrentUser).ToList();

        if (machine.Count > 0 || createRestorePoint)
        {
            var request = new ElevatedRequest { Ops = machine, CreateRestorePoint = createRestorePoint };
            var response = IsElevated ? ElevatedHost.Execute(request) : await RunElevatedAsync(request, ct);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "elevated-failed");
            if (createRestorePoint && response.RestorePointCreated != true)
                throw new RestorePointException(response.Error ?? "restore-point-failed");
        }

        if (user.Count > 0)
            WindowsRegistry.Execute(user);
        Broadcast();
    }

    private async Task<ElevatedResponse> RunElevatedAsync(ElevatedRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(stateDirectory);
        var file = Path.Combine(stateDirectory, $"elevated-{Guid.NewGuid():N}.json");
        var responseFile = file + ".result";
        await Json.WriteFileAtomicAsync(file, request, ct);
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { ElevatedHost.Argument, file },
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process? p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new ElevationCancelledException();
            }
            if (p is null) throw new InvalidOperationException("elevated-failed");
            using (p)
                await p.WaitForExitAsync(ct);
            if (!File.Exists(responseFile))
                return new ElevatedResponse { Ok = false, Error = "elevated-no-response" };
            return await Json.ReadFileAsync<ElevatedResponse>(responseFile, ct) ?? new ElevatedResponse { Ok = false };
        }
        finally
        {
            try { File.Delete(file); } catch { }
            try { File.Delete(responseFile); } catch { }
        }
    }

    /// <summary>Avisa a Windows y al Explorador de que cambió la configuración.</summary>
    public static void Broadcast()
    {
        foreach (var area in new[] { "ImmersiveColorSet", "Policy", "WindowsThemeElement" })
            _ = NativeMethods.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, area, 0x0002, 200, out _);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}

/// <summary>Lo que hace la copia elevada de la aplicación (<c>--elevated archivo</c>).</summary>
public static class ElevatedHost
{
    public const string Argument = "--elevated";

    public static int Run(string requestFile)
    {
        ElevatedResponse response;
        try
        {
            var request = Json.Deserialize<ElevatedRequest>(File.ReadAllText(requestFile)) ?? new ElevatedRequest();
            response = Execute(request);
        }
        catch (Exception ex)
        {
            response = new ElevatedResponse { Ok = false, Error = ex.Message };
        }
        File.WriteAllText(requestFile + ".result", Json.Serialize(response));
        return response.Ok ? 0 : 1;
    }

    public static ElevatedResponse Execute(ElevatedRequest request)
    {
        var rejected = request.Ops.FirstOrDefault(o => !TweakCatalog.IsAllowed(o));
        if (rejected is not null)
        {
            Log.Warn("Elevated request rejected: operation outside the catalog");
            return new ElevatedResponse { Ok = false, Error = "op-not-allowed" };
        }

        var response = new ElevatedResponse { Ok = true };
        if (request.CreateRestorePoint)
        {
            var (ok, error) = CreateRestorePoint();
            response.RestorePointCreated = ok;
            if (!ok)
            {
                // Sin punto de restauración no se cambia nada: el usuario lo pidió.
                response.Ok = false;
                response.Error = error;
                return response;
            }
        }

        try
        {
            WindowsRegistry.Execute(request.Ops);
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }
        return response;
    }

    private static (bool Ok, string? Error) CreateRestorePoint()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Checkpoint-Computer -Description 'Windows Reborn' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop",
                },
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(180_000);
            Log.Info($"Restore point exit code {p.ExitCode}");
            return p.ExitCode == 0 ? (true, null) : (false, "restore-point-failed: " + err.Trim().Split('\n')[0]);
        }
        catch (Exception ex)
        {
            return (false, "restore-point-failed: " + ex.Message);
        }
    }
}
