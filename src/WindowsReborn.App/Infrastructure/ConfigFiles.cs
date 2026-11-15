using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;

namespace WindowsReborn.App.Infrastructure;

/// <summary>Diálogos comunes para guardar y cargar configuraciones (.zip) de una sección.</summary>
public static class ConfigFiles
{
    /// <summary>Guarda la configuración; devuelve el texto de estado o null si se canceló.</summary>
    public static async Task<string?> SaveAsync(MyConfig config, string fileNameKey)
    {
        config.AppVersion = AppServices.Version;
        config.CreatedUtc = DateTime.UtcNow;
        var path = Dialogs.SaveFile(Loc.T("section.save"), Loc.T("config.filter") + "|*.zip", Loc.T(fileNameKey) + ".zip");
        if (path is null) return null;
        try
        {
            await config.SaveZipAsync(path);
            return Loc.T("config.exported", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            return Loc.T("config.error", ex.Message);
        }
    }

    /// <summary>Pide un .zip y lo lee; null si se canceló o no es válido (ya avisa al usuario).</summary>
    public static async Task<MyConfig?> LoadAsync()
    {
        var path = Dialogs.OpenFile(Loc.T("section.load"), Loc.T("config.filter") + "|*.zip;*.json");
        if (path is null) return null;
        try
        {
            return await MyConfig.LoadAsync(path);
        }
        catch (Exception)
        {
            await Dialogs.InfoAsync(Loc.T("section.load"), Loc.T("config.invalid"));
            return null;
        }
    }
}
