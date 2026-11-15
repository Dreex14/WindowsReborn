namespace WindowsReborn.Core.Paths;

/// <summary>
/// Ubicaciones de datos de la propia aplicación. En modo portátil (existe
/// <c>portable.flag</c> junto al ejecutable) todo se guarda en <c>.\Data</c>; si no,
/// en <c>%LOCALAPPDATA%\WindowsReborn</c>.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string DataRoot { get; }
    public string ConfigDirectory => Path.Combine(DataRoot, "config");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string ReportsDirectory => Path.Combine(DataRoot, "reports");
    public string DownloadsDirectory => Path.Combine(DataRoot, "downloads");

    public static AppPaths Detect()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.flag")))
            return new AppPaths(Path.Combine(exeDir, "Data"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "WindowsReborn"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ReportsDirectory);
    }
}
