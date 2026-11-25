using System.Runtime.CompilerServices;

namespace WindowsReborn.App;

/// <summary>
/// Punto de entrada. La copia elevada (<c>--elevated</c>) se atiende aquí, antes de cargar WPF:
/// así el proceso con permisos de administrador no carga ninguna de las DLL nativas que el
/// .exe portátil extrae en la carpeta temporal del usuario (que cualquier programa del usuario
/// podría sustituir).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var elevated = Array.IndexOf(args, Tweaks.ElevatedHost.Argument);
        if (elevated >= 0)
            return elevated + 2 < args.Length ? Tweaks.ElevatedHost.Run(args[elevated + 1], args[elevated + 2]) : Tweaks.ElevatedHost.BadRequest;
        return RunApp();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
