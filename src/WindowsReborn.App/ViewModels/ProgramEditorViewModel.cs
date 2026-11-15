using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsReborn.App.Infrastructure;
using WindowsReborn.Core.Catalog;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Localization;
using WindowsReborn.Programs;

namespace WindowsReborn.App.ViewModels;

public partial class AdapterChoice : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public partial class ProgramEditorViewModel : ObservableObject
{
    private readonly ProgramEntry _original;
    private CancellationTokenSource? _cts;

    public ProgramEditorViewModel(ProgramEntry? existing)
    {
        // Se trabaja sobre una copia: si se cancela, la entrada original no cambia.
        _original = existing is null ? new ProgramEntry() : Clone(existing);
        IsNew = existing is null;
        _name = _original.Name;
        _homepage = _original.Homepage ?? "";
        _winGetId = _original.WinGetId ?? "";
        _gitHubRepo = _original.Source.GitHubRepo ?? "";
        _assetPattern = _original.Source.AssetPattern ?? "";
        _directUrl = _original.Source.DirectUrl ?? "";
        _vendorAdapter = _original.Source.VendorAdapter ?? VendorAdapters.All.FirstOrDefault() ?? "";
        _expectedPublisher = _original.ExpectedPublisher ?? "";
        _detectName = _original.DetectName ?? "";
        _notes = _original.Notes ?? "";
        _sourceKind = SourceKinds.First(k => k.Value == _original.Source.Kind);
        _architecture = Architectures.FirstOrDefault(a => a.Value == _original.Architecture) ?? Architectures[0];
        _installerType = InstallerTypes.FirstOrDefault(a => a.Value == _original.InstallerType) ?? InstallerTypes[0];
        _installMode = InstallModes.First(m => m.Value == _original.InstallMode);
        _trust = _original.Source.Trust;
        _trustReason = _original.Source.TrustReason;

        foreach (var a in AppConfigCatalog.All)
            ConfigAdapters.Add(new AdapterChoice { Id = a.Id, Name = a.Name, IsSelected = _original.ConfigAdapters.Contains(a.Id) });
    }

    public bool IsNew { get; }
    public string WindowTitle => IsNew ? Loc.T("editor.title-new") : Loc.T("editor.title-edit");

    public IReadOnlyList<Option<SourceKind>> SourceKinds { get; } =
    [
        new(Programs.SourceKind.WinGet, Loc.T("kind.winget")),
        new(Programs.SourceKind.GitHub, Loc.T("kind.github")),
        new(Programs.SourceKind.Vendor, Loc.T("kind.vendor")),
        new(Programs.SourceKind.DirectUrl, Loc.T("kind.url")),
        new(Programs.SourceKind.None, Loc.T("kind.none")),
    ];

    public IReadOnlyList<Option<string>> Architectures { get; } =
        [new("x64", "x64"), new("arm64", "ARM64"), new("x86", "x86 (32 bits)"), new("neutral", Loc.T("arch.neutral"))];

    public IReadOnlyList<Option<string>> InstallerTypes { get; } =
        [new("any", Loc.T("type.any")), new("exe", "EXE"), new("msi", "MSI"), new("msix", "MSIX"), new("zip", "ZIP")];

    public IReadOnlyList<Option<InstallMode>> InstallModes { get; } =
    [
        new(Programs.InstallMode.Manual, Loc.T("mode.manual")),
        new(Programs.InstallMode.Automated, Loc.T("mode.automated")),
        new(Programs.InstallMode.DownloadOnly, Loc.T("mode.download")),
    ];

    public IReadOnlyList<string> VendorAdapterIds => VendorAdapters.All;
    public ObservableCollection<WinGetPackage> SearchResults { get; } = [];
    public ObservableCollection<AdapterChoice> ConfigAdapters { get; } = [];
    public ObservableCollection<string> IdentifyNotes { get; } = [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _homepage;
    [ObservableProperty] private string _winGetId;
    [ObservableProperty] private string _gitHubRepo;
    [ObservableProperty] private string _assetPattern;
    [ObservableProperty] private string _directUrl;
    [ObservableProperty] private string _vendorAdapter;
    [ObservableProperty] private string _expectedPublisher;
    [ObservableProperty] private string _detectName;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private Option<SourceKind> _sourceKind;
    [ObservableProperty] private Option<string> _architecture;
    [ObservableProperty] private Option<string> _installerType;
    [ObservableProperty] private Option<InstallMode> _installMode;
    [ObservableProperty] private SourceTrust _trust;
    [ObservableProperty] private string? _trustReason;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _userUrl = "";
    [ObservableProperty] private WinGetPackage? _selectedResult;

    public bool IsWinGet => SourceKind.Value == Programs.SourceKind.WinGet;
    public bool IsGitHub => SourceKind.Value == Programs.SourceKind.GitHub;
    public bool IsVendor => SourceKind.Value == Programs.SourceKind.Vendor;
    public bool IsDirectUrl => SourceKind.Value == Programs.SourceKind.DirectUrl;
    public string TrustText => Loc.T("trust." + Trust.ToString().ToLowerInvariant()) + (TrustReason is null ? "" : " — " + Loc.T(TrustReason));

    partial void OnSourceKindChanged(Option<SourceKind> value)
    {
        OnPropertyChanged(nameof(IsWinGet));
        OnPropertyChanged(nameof(IsGitHub));
        OnPropertyChanged(nameof(IsVendor));
        OnPropertyChanged(nameof(IsDirectUrl));
        // Cambiar de fuente a mano la convierte en «elegida por el usuario» hasta que se vuelva a contrastar.
        if (value.Value != _original.Source.Kind)
        {
            Trust = value.Value == Programs.SourceKind.Vendor ? SourceTrust.Verified : SourceTrust.UserSelected;
            TrustReason = value.Value == Programs.SourceKind.Vendor ? "trust.vendor-adapter" : "trust.user-changed";
        }
    }

    partial void OnTrustChanged(SourceTrust value) => OnPropertyChanged(nameof(TrustText));
    partial void OnTrustReasonChanged(string? value) => OnPropertyChanged(nameof(TrustText));

    partial void OnGitHubRepoChanged(string value) => MarkUserChanged(Programs.SourceKind.GitHub, _original.Source.GitHubRepo, value);
    partial void OnDirectUrlChanged(string value) => MarkUserChanged(Programs.SourceKind.DirectUrl, _original.Source.DirectUrl, value);

    private void MarkUserChanged(SourceKind kind, string? original, string value)
    {
        if (SourceKind.Value == kind && !string.Equals(original ?? "", value, StringComparison.OrdinalIgnoreCase) && Trust == SourceTrust.Verified)
        {
            Trust = SourceTrust.UserSelected;
            TrustReason = "trust.user-changed";
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Error = null;
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = Loc.T("editor.name-required");
            return;
        }
        IsSearching = true;
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var results = await AppServices.Identifier.SearchAsync(Name.Trim(), _cts.Token);
            foreach (var r in results.Take(30)) SearchResults.Add(r);
            if (SearchResults.Count == 0) Error = Loc.T("editor.no-results");
        }
        catch (Exception ex)
        {
            Error = ProgramsViewModel.DescribeError(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    partial void OnSelectedResultChanged(WinGetPackage? value)
    {
        if (value is not null) _ = ApplyProposalAsync(value);
    }

    private async Task ApplyProposalAsync(WinGetPackage package)
    {
        Error = null;
        IdentifyNotes.Clear();
        IsSearching = true;
        try
        {
            var proposal = await AppServices.Identifier.ProposeAsync(package.Id, string.IsNullOrWhiteSpace(Homepage) ? null : Homepage, CancellationToken.None);
            WinGetId = package.Id;
            if (string.IsNullOrWhiteSpace(Homepage) && proposal.Homepage is not null) Homepage = proposal.Homepage;
            if (string.IsNullOrWhiteSpace(DetectName)) DetectName = package.Name;

            SourceKind = SourceKinds.First(k => k.Value == proposal.Source.Kind);
            GitHubRepo = proposal.Source.GitHubRepo ?? GitHubRepo;
            if (proposal.Source.VendorAdapter is not null) VendorAdapter = proposal.Source.VendorAdapter;
            Trust = proposal.Source.Trust;
            TrustReason = proposal.Source.TrustReason;
            if (proposal.Source.Kind == Programs.SourceKind.Vendor && proposal.Source.VendorAdapter == MozillaFirefoxAdapter.Id)
                ExpectedPublisher = "Mozilla Corporation";
            if (proposal.Source.Kind == Programs.SourceKind.WinGet && InstallMode.Value == Programs.InstallMode.Manual)
                InstallMode = InstallModes.First(m => m.Value == Programs.InstallMode.Automated);

            IdentifyNotes.Add(Loc.T("editor.proposal", SourceKinds.First(k => k.Value == proposal.Source.Kind).Label, proposal.LatestVersion ?? "?"));
            foreach (var n in proposal.Notes) IdentifyNotes.Add(Loc.T(n));
        }
        catch (Exception ex)
        {
            Error = ProgramsViewModel.DescribeError(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void UseUserUrl()
    {
        Error = null;
        try
        {
            var source = SourceIdentifier.FromUserUrl(UserUrl.Trim(), string.IsNullOrWhiteSpace(Homepage) ? null : Homepage);
            SourceKind = SourceKinds.First(k => k.Value == source.Kind);
            if (source.Kind == Programs.SourceKind.GitHub) GitHubRepo = source.GitHubRepo!;
            else DirectUrl = source.DirectUrl!;
            Trust = source.Trust;
            TrustReason = source.TrustReason;
        }
        catch (InvalidOperationException)
        {
            Error = Loc.T("editor.https-only");
        }
    }

    [RelayCommand]
    private void OpenHomepage()
    {
        if (!string.IsNullOrWhiteSpace(Homepage)) Dialogs.OpenUri(Homepage);
    }

    /// <summary>Valida y devuelve la entrada resultante, o null con <see cref="Error"/> relleno.</summary>
    public ProgramEntry? TryBuild()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Name)) { Error = Loc.T("editor.name-required"); return null; }
        if (!string.IsNullOrWhiteSpace(Homepage) && !IsHttps(Homepage)) { Error = Loc.T("editor.homepage-https"); return null; }

        var kind = SourceKind.Value;
        switch (kind)
        {
            case Programs.SourceKind.WinGet when string.IsNullOrWhiteSpace(WinGetId):
                Error = Loc.T("editor.winget-required"); return null;
            case Programs.SourceKind.GitHub when !System.Text.RegularExpressions.Regex.IsMatch(GitHubRepo.Trim(), @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"):
                Error = Loc.T("editor.repo-invalid"); return null;
            case Programs.SourceKind.DirectUrl when !IsHttps(DirectUrl):
                Error = Loc.T("editor.https-only"); return null;
            case Programs.SourceKind.Vendor when string.IsNullOrWhiteSpace(VendorAdapter):
                Error = Loc.T("editor.vendor-required"); return null;
        }
        if (InstallMode.Value == Programs.InstallMode.Automated && string.IsNullOrWhiteSpace(WinGetId))
        {
            Error = Loc.T("editor.automated-needs-winget");
            return null;
        }

        var entry = _original;
        entry.Name = Name.Trim();
        entry.Homepage = NullIfEmpty(Homepage);
        entry.WinGetId = NullIfEmpty(WinGetId);
        entry.Architecture = Architecture.Value;
        entry.InstallerType = InstallerType.Value;
        entry.InstallMode = InstallMode.Value;
        entry.ExpectedPublisher = NullIfEmpty(ExpectedPublisher);
        entry.DetectName = NullIfEmpty(DetectName);
        entry.Notes = NullIfEmpty(Notes);
        entry.ConfigAdapters = ConfigAdapters.Where(a => a.IsSelected).Select(a => a.Id).ToList();
        entry.Source = new ProgramSource
        {
            Kind = kind,
            Trust = kind == Programs.SourceKind.None ? SourceTrust.Unverified : Trust,
            TrustReason = TrustReason,
            GitHubRepo = kind == Programs.SourceKind.GitHub ? GitHubRepo.Trim() : null,
            AssetPattern = kind == Programs.SourceKind.GitHub ? NullIfEmpty(AssetPattern) : null,
            VendorAdapter = kind == Programs.SourceKind.Vendor ? VendorAdapter : null,
            DirectUrl = kind == Programs.SourceKind.DirectUrl ? DirectUrl.Trim() : null,
        };
        return entry;
    }

    private static bool IsHttps(string s) => Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ProgramEntry Clone(ProgramEntry e) =>
        JsonSerializer.Deserialize<ProgramEntry>(JsonSerializer.Serialize(e, Json.Options), Json.Options)!;
}
