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
        // HKCU\Software\Policies solo se puede escribir con permisos de administrador.
        var machine = ops.Where(NeedsElevation).ToList();
        var user = ops.Where(o => !NeedsElevation(o)).ToList();

        if (machine.Count > 0 || createRestorePoint)
        {
            var request = new ElevatedRequest { Ops = machine, CreateRestorePoint = createRestorePoint };
            var response = IsElevated ? ElevatedHost.Execute(request) : await RunElevatedAsync(request, ct);
            // Primero el punto de restauración: si falló, no se cambió nada y se ofrece seguir sin él.
            if (createRestorePoint && response.RestorePointCreated == false)
                throw new RestorePointException(response.Error ?? "restore-point-failed");
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "elevated-failed");
        }

        if (user.Count > 0)
            WindowsRegistry.Execute(user);
        Broadcast();
    }

    public static bool NeedsElevation(RegOp op) =>
        AdminKeys.Requires(op.Hive, op.Key);

    private async Task<ElevatedResponse> RunElevatedAsync(ElevatedRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(stateDirectory);
        var file = Path.Combine(stateDirectory, $"elevated-{Guid.NewGuid():N}.json");
        var bytes = System.Text.Encoding.UTF8.GetBytes(Json.Serialize(request));
        await File.WriteAllBytesAsync(file, bytes, ct);
        // El hash va en la línea de comandos: si alguien cambia el archivo después de que el
        // usuario acepte el aviso de Windows, la copia elevada lo rechaza.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { ElevatedHost.Argument, file, hash },
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
            int code;
            using (p)
            {
                await p.WaitForExitAsync(ct);
                code = p.ExitCode;
            }
            return ElevatedHost.FromExitCode(code, request.CreateRestorePoint);
        }
        finally
        {
            try { File.Delete(file); } catch { }
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

    // Códigos de salida: la copia elevada nunca escribe en carpetas del usuario.
    public const int Ok = 0, Failed = 1, NotAllowed = 2, RestorePointFailed = 3, BadRequest = 4;
    private const int MaxRequestBytes = 1 << 20;

    public static int Run(string requestFile, string expectedSha256)
    {
        try
        {
            var info = new FileInfo(requestFile);
            if (!info.Exists || info.Length > MaxRequestBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return BadRequest;
            var bytes = File.ReadAllBytes(requestFile);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return BadRequest;
            var request = Json.Deserialize<ElevatedRequest>(System.Text.Encoding.UTF8.GetString(bytes));
            if (request is null) return BadRequest;
            return ToExitCode(Execute(request));
        }
        catch
        {
            return Failed;
        }
    }

    internal static int ToExitCode(ElevatedResponse r) =>
        r.Ok ? Ok : r.Error == "op-not-allowed" ? NotAllowed : r.RestorePointCreated == false ? RestorePointFailed : Failed;

    public static ElevatedResponse FromExitCode(int code, bool restorePointRequested) => code switch
    {
        Ok => new ElevatedResponse { Ok = true, RestorePointCreated = restorePointRequested ? true : null },
        NotAllowed => new ElevatedResponse { Ok = false, Error = "op-not-allowed" },
        RestorePointFailed => new ElevatedResponse { Ok = false, Error = "restore-point-failed", RestorePointCreated = false },
        BadRequest => new ElevatedResponse { Ok = false, Error = "elevated-bad-request" },
        _ => new ElevatedResponse { Ok = false, Error = "elevated-failed" },
    };

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
                    "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue; " +
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
