using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsReborn.App.Infrastructure;

/// <summary>Iconos de los programas: incluidos en el .exe (catálogo) o extraídos del .exe instalado (GitHub).</summary>
public static class ProgramIcons
{
    public static ImageSource? For(string catalogId)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Programs/{catalogId}.png");
            if (Application.GetResourceStream(uri) is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 64;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Icono de un .exe del disco (null si no existe).</summary>
    public static ImageSource? FromExe(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
