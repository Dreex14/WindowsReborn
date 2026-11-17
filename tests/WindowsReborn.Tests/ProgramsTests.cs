using System.Net;
using System.Text;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Programs;

namespace WindowsReborn.Tests;

public class ProgramsTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var r = respond(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> hidden) : IProcessRunner
    {
        public List<string> VisibleCalls { get; } = [];
        public Task<ProcessResult> RunHiddenAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(hidden(args));
        public Task<int> RunVisibleAsync(string exe, string arguments, CancellationToken ct) { VisibleCalls.Add(arguments); return Task.FromResult(0); }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    [Fact]
    public void Parses_winget_search_table_with_translated_headers()
    {
        const string output = "   - \r   \\ \r" +
            "Nombre                                  Id                        Versión    Coincidencia  Origen\n" +
            "-------------------------------------------------------------------------------------------------\n" +
            "Mozilla Firefox (es-ES)                 Mozilla.Firefox.es-ES     160.0      Tag: firefox  winget\n" +
            "Visual Studio Code                      Microsoft.VisualStudioCode 1.106.0                 winget\n";
        var result = WinGetClient.ParseTable(output);
        Assert.Equal(2, result.Count);
        Assert.Equal("Mozilla.Firefox.es-ES", result[0].Id);
        Assert.Equal("160.0", result[0].Version);
    }

    [Fact]
    public async Task WinGet_errors_are_surfaced_not_hidden()
    {
        var client = new WinGetClient(new FakeRunner(_ => new ProcessResult(unchecked((int)0x8A15000F), "source error")));
        var ex = await Assert.ThrowsAsync<WinGetException>(() => client.SearchAsync("x", CancellationToken.None));
        Assert.Contains("source error", ex.Output);
    }

    [Fact]
    public async Task WinGet_not_installed_is_reported_as_false()
    {
        var client = new WinGetClient(new FakeRunner(_ => new ProcessResult(WinGetClient.NoPackagesFound, "")));
        Assert.False(await client.IsInstalledAsync("Some.App", CancellationToken.None));
    }

    [Fact]
    public void Install_arguments_never_accept_agreements_silently()
    {
        var args = WinGetClient.InstallArguments(new ProgramEntry { WinGetId = "Mozilla.Firefox", Architecture = "x64" }, silent: true);
        Assert.DoesNotContain("accept", args, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--exact", args);
    }

    [Fact]
    public async Task GitHub_adapter_requires_unambiguous_asset_and_uses_digest()
    {
        const string json = """
        {"tag_name":"v2.5.0","assets":[
          {"name":"app-2.5.0-x64.msi","browser_download_url":"https://github.com/owner/app/releases/download/v2.5.0/app-2.5.0-x64.msi","digest":"sha256:abc123"},
          {"name":"app-2.5.0-arm64.msi","browser_download_url":"https://github.com/owner/app/releases/download/v2.5.0/app-2.5.0-arm64.msi"}
        ]}
        """;
        var http = new HttpClient(new FakeHandler(_ => Ok(json)));
        var adapter = new GitHubReleasesAdapter(http);

        var entry = new ProgramEntry { Name = "App", Source = new ProgramSource { Kind = SourceKind.GitHub, GitHubRepo = "owner/app" } };
        var ex = await Assert.ThrowsAsync<SourceException>(() => adapter.GetLatestAsync(entry, CancellationToken.None));
        Assert.Equal("source.pattern-required", ex.Key);

        entry.Source.AssetPattern = "*-x64.msi";
        var release = await adapter.GetLatestAsync(entry, CancellationToken.None);
        Assert.Equal("2.5.0", release.Version);
        Assert.Equal("abc123", release.ExpectedSha256);
    }

    [Fact]
    public async Task GitHub_adapter_rejects_assets_hosted_elsewhere()
    {
        const string json = """{"tag_name":"1.0","assets":[{"name":"a.exe","browser_download_url":"https://evil.example/a.exe"}]}""";
        var adapter = new GitHubReleasesAdapter(new HttpClient(new FakeHandler(_ => Ok(json))));
        var entry = new ProgramEntry { Name = "A", Source = new ProgramSource { Kind = SourceKind.GitHub, GitHubRepo = "o/a", AssetPattern = "a.exe" } };
        var ex = await Assert.ThrowsAsync<SourceException>(() => adapter.GetLatestAsync(entry, CancellationToken.None));
        Assert.Equal("source.unexpected-url", ex.Key);
    }

    [Fact]
    public async Task Mozilla_adapter_reads_version_and_official_hash()
    {
        var http = new HttpClient(new FakeHandler(req => req.RequestUri!.Host switch
        {
            "product-details.mozilla.org" => Ok("""{"LATEST_FIREFOX_VERSION":"160.0"}"""),
            _ => Ok("aaaa  win64/en-US/Firefox Setup 160.0.exe\nbbbb  win64/es-ES/Firefox Setup 160.0.exe\n"),
        }));
        var adapter = new MozillaFirefoxAdapter(http, () => "es");
        var entry = new ProgramEntry { Name = "Firefox", Source = new ProgramSource { Kind = SourceKind.Vendor, VendorAdapter = MozillaFirefoxAdapter.Id } };
        var release = await adapter.GetLatestAsync(entry, CancellationToken.None);
        Assert.Equal("160.0", release.Version);
        Assert.Equal("bbbb", release.ExpectedSha256);
        Assert.Equal("Mozilla Corporation", release.ExpectedPublisher);
        Assert.StartsWith("https://download-installer.cdn.mozilla.net/", release.DownloadUrl);
    }

    [Fact]
    public async Task Download_with_wrong_hash_is_deleted()
    {
        using var t = new TempDir();
        var http = new HttpClient(new FakeHandler(_ => Ok("installer bytes")));
        var downloader = new Downloader(http, new WinGetClient(new FakeRunner(_ => new ProcessResult(0, ""))));
        var entry = new ProgramEntry { Name = "Tool" };
        var release = new ReleaseInfo { DownloadUrl = "https://example.org/tool.zip", FileName = "tool.zip", ExpectedSha256 = "0000" };
        var ex = await Assert.ThrowsAsync<SourceException>(() => downloader.DownloadAsync(entry, release, t.Root, null, CancellationToken.None));
        Assert.Equal("download.hash-mismatch", ex.Key);
        Assert.Empty(Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_rejects_plain_http()
    {
        using var t = new TempDir();
        var downloader = new Downloader(new HttpClient(new FakeHandler(_ => Ok("x"))), new WinGetClient(new FakeRunner(_ => new ProcessResult(0, ""))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(new ProgramEntry { Name = "x" },
            new ReleaseInfo { DownloadUrl = "http://example.org/x.zip" }, t.Root, null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_with_matching_hash_is_kept()
    {
        using var t = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("zip-content");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var downloader = new Downloader(http, new WinGetClient(new FakeRunner(_ => new ProcessResult(0, ""))));
        var result = await downloader.DownloadAsync(new ProgramEntry { Name = "Tool" },
            new ReleaseInfo { DownloadUrl = "https://example.org/tool.zip", FileName = "tool.zip", ExpectedSha256 = sha }, t.Root, null, CancellationToken.None);
        Assert.True(result.HashVerified);
        Assert.Equal(SignatureStatus.NotApplicable, result.Signature);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void User_url_is_classified_without_being_replaced()
    {
        var gh = SourceIdentifier.FromUserUrl("https://github.com/owner/repo", null);
        Assert.Equal(SourceKind.GitHub, gh.Kind);
        Assert.Equal("owner/repo", gh.GitHubRepo);
        Assert.Equal(SourceTrust.UserSelected, gh.Trust);

        var same = SourceIdentifier.FromUserUrl("https://download.example.com/app.exe", "https://www.example.com");
        Assert.Equal(SourceKind.DirectUrl, same.Kind);
        Assert.Equal("https://download.example.com/app.exe", same.DirectUrl);
        Assert.Equal(SourceTrust.UserSelected, same.Trust);

        var other = SourceIdentifier.FromUserUrl("https://mirror.other.net/app.exe", "https://www.example.com");
        Assert.Equal(SourceTrust.Unverified, other.Trust);
    }

    [Fact]
    public void Import_never_replaces_existing_sources()
    {
        var target = new ProgramLibrary { Programs = [new ProgramEntry { Name = "App", WinGetId = "Org.App", Source = new ProgramSource { Kind = SourceKind.WinGet } }] };
        var incoming = new ProgramLibrary
        {
            Programs =
            [
                new ProgramEntry { Name = "App", WinGetId = "Org.App", Source = new ProgramSource { Kind = SourceKind.DirectUrl, DirectUrl = "https://x.org/a.exe" } },
                new ProgramEntry { Name = "Other" },
            ],
        };
        var outcome = ProgramLibraryStore.Merge(target, incoming);
        Assert.Equal(1, outcome.Added);
        Assert.Single(outcome.ConflictingSources);
        Assert.Equal(SourceKind.WinGet, target.Programs[0].Source.Kind);
    }

    [Fact]
    public async Task Library_roundtrip_and_export_drop_local_paths()
    {
        using var t = new TempDir();
        var store = new ProgramLibraryStore(t.Root);
        var lib = new ProgramLibrary { Programs = [new ProgramEntry { Name = "App", Homepage = "https://app.org", LastDownloadPath = @"C:\Users\x\a.exe" }] };
        await store.SaveAsync(lib);
        var loaded = await store.LoadAsync();
        Assert.Equal("https://app.org", loaded.Programs.Single().Homepage);

        var export = Path.Combine(t.Root, "list.json");
        await ProgramLibraryStore.ExportAsync(loaded, export);
        Assert.DoesNotContain("Users", File.ReadAllText(export));
        var read = await ProgramLibraryStore.ReadListAsync(export);
        Assert.Equal("App", read.Programs.Single().Name);
    }

    [Fact]
    public void Config_package_only_accepts_whitelisted_json()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["settings.json"] = Encoding.UTF8.GetBytes("{}"),
            ["programs.json"] = Encoding.UTF8.GetBytes("{\"programs\":[]}"),
            ["profiles/semanal.json"] = Encoding.UTF8.GetBytes("{}"),
            ["../evil.json"] = Encoding.UTF8.GetBytes("{}"),
            ["profiles/../../evil.json"] = Encoding.UTF8.GetBytes("{}"),
            ["cookies.sqlite"] = [1, 2, 3],
            ["tweaks.json"] = Encoding.UTF8.GetBytes("not json"),
        };
        var package = ConfigStore.ReadPackage(files);
        Assert.Equal(3, package.Files.Count);
        Assert.Equal(4, package.Rejected.Count);
    }

    [Fact]
    public void Registrable_domain_comparison()
    {
        Assert.True(Http.SameSite("https://download.mozilla.org/x", "https://www.mozilla.org"));
        Assert.False(Http.SameSite("https://mozilla.org.evil.com", "https://www.mozilla.org"));
        Assert.True(Http.SameSite("https://a.example.co.uk", "https://b.example.co.uk"));
    }

    [Fact]
    public void Installed_detection_matches_by_name()
    {
        var apps = new[] { new InstalledApp("Mozilla Firefox (x64 es-ES)", "160.0", "Mozilla"), new InstalledApp("7-Zip 24.08 (x64)", "24.08", "Igor Pavlov") };
        Assert.Equal("160.0", InstalledDetector.Match(apps, "Mozilla Firefox")?.Version);
        Assert.Null(InstalledDetector.Match(apps, "Visual Studio Code"));
    }
}
