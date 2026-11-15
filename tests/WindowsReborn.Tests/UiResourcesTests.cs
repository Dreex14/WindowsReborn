using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsReborn.Tests;

/// <summary>
/// Comprobaciones estáticas de la interfaz: iconos y textos que solo fallarían al
/// abrir la pantalla correspondiente.
/// </summary>
public partial class UiResourcesTests
{
    private static string AppFolder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowsReborn.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "WindowsReborn.App");
        }
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(AppFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

    [GeneratedRegex(@"SymbolIcon (\w+)\}|Symbol=""(\w+)""|""([A-Z]\w+(?:16|20|24|28|32))""")]
    private static partial Regex IconPattern();

    [GeneratedRegex(@"\{l:Tr ([\w.\-]+)\}|Loc\.T\(""([\w.\-]+)""|ConverterParameter=([\w]+\.[\w.\-]+)\}")]
    private static partial Regex KeyPattern();

    [Fact]
    public void All_icons_exist_in_wpf_ui()
    {
        var names = SourceFiles().SelectMany(f => IconPattern().Matches(File.ReadAllText(f)))
            .Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value)
            .Distinct().ToList();
        Assert.NotEmpty(names);
        var missing = names.Where(n => !Enum.IsDefined(typeof(Wpf.Ui.Controls.SymbolRegular), n)).ToList();
        Assert.True(missing.Count == 0, "Unknown icons: " + string.Join(", ", missing));
    }

    [Fact]
    public void All_literal_translation_keys_exist_in_both_languages()
    {
        var core = Path.Combine(Path.GetDirectoryName(AppFolder)!, "WindowsReborn.Core", "Localization");
        var es = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(core, "es.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(core, "en.json")))!;
        var keys = SourceFiles().SelectMany(f => KeyPattern().Matches(File.ReadAllText(f)))
            .Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value)
            .Where(k => !k.EndsWith('.')) // prefijos de claves dinámicas: los cubre Dynamic_translation_keys_exist
            .Distinct().ToList();
        Assert.True(keys.Count > 200, $"only {keys.Count} keys found");
        var missing = keys.Where(k => !es.ContainsKey(k) || !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing translations: " + string.Join(", ", missing));
    }

    [Fact]
    public void Dynamic_translation_keys_exist()
    {
        var es = Core.Localization.Loc.LoadEmbedded("es");
        var en = Core.Localization.Loc.LoadEmbedded("en");
        var keys = new List<string>();
        keys.AddRange(Enum.GetNames<Tweaks.TweakCategory>().Select(c => "tweakcat." + c.ToLowerInvariant()));
        keys.AddRange(new[] { "startup", "appx", "all" }.Select(c => "tweakcat." + c));
        keys.AddRange(Enum.GetValues<Tweaks.RiskLevel>().Select(r => $"risk.{(int)r}"));
        keys.AddRange(Enum.GetNames<Tweaks.RestartNeed>().Where(r => r != "None").SelectMany(r => new[] { "restart." + r.ToLowerInvariant(), "restart.pending." + r.ToLowerInvariant() }));
        keys.AddRange(Enum.GetNames<Tweaks.StartupLocation>().Select(l => "startup.loc." + l.ToLowerInvariant()));
        keys.AddRange(App.ViewModels.RestoreViewModel.StepKeys.Select(k => "wizard." + k));
        keys.AddRange(new[] { "pending", "done", "skipped" }.Select(s => "wizard.state." + s));
        keys.AddRange(new[] { "exists", "integrity", "custom", "config", "programs", "installers", "location", "recoverable" }.Select(k => "prepare.check." + k));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(AppFolder)!, "WindowsReborn.Backup"), "FormatReadiness.cs"))
            keys.AddRange(DetailPattern().Matches(File.ReadAllText(file)).Select(m => "prepare.detail." + m.Groups[1].Value));
        var missing = keys.Where(k => !es.ContainsKey(k) || !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing translations: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"CheckState\.\w+, ""([\w.\-]+)""")]
    private static partial Regex DetailPattern();
}
