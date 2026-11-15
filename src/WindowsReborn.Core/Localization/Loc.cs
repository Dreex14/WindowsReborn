using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowsReborn.Core.Localization;

/// <summary>
/// Textos de la interfaz en español e inglés. El idioma se elige según la
/// distribución del teclado activa: si es española (cualquier variante) se usa
/// español; en cualquier otro caso, inglés.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private const int LangSpanish = 0x0A;
    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();

    public static Loc Instance { get; } = new();

    public string Language { get; private set; } = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Indexador usado desde XAML: <c>{Binding [Clave], Source={x:Static loc:Loc.Instance}}</c>.</summary>
    public string this[string key] => Get(key);

    public static string T(string key) => Instance.Get(key);

    public static string T(string key, params object[] args)
    {
        var format = Instance.Get(key);
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; }
    }

    public string Get(string key) =>
        _strings.TryGetValue(key, out var v) ? v :
        _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Carga el idioma indicado (<c>es</c>, <c>en</c> o <c>auto</c>).</summary>
    public void Load(string preference)
    {
        Language = preference switch
        {
            "es" => "es",
            "en" => "en",
            _ => DetectFromKeyboard(),
        };
        _fallback = LoadEmbedded("en");
        _strings = Language == "en" ? _fallback : LoadEmbedded(Language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public static string DetectFromKeyboard()
    {
        try
        {
            var layout = GetKeyboardLayout(0).ToInt64();
            return (layout & 0x3FF) == LangSpanish ? "es" : "en";
        }
        catch
        {
            return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
        }
    }

    internal static Dictionary<string, string> LoadEmbedded(string language)
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"WindowsReborn.Core.Localization.{language}.json");
        if (stream is null)
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
