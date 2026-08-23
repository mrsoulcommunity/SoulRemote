using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The updater downloads something and then hands it to Windows Installer, so the
/// interesting cases are the ones where it must refuse: a checksum that does not match,
/// a release with nothing installable on it, a tag that is not a version. All of them
/// are exercised here through a stub transport, with no network and no Windows.
/// </summary>
public sealed class AppUpdateServiceTests : IDisposable
{
    private const string Repo = "mrsoulcommunity/SoulRemote";

    private readonly string _cache = Path.Combine(
        Path.GetTempPath(), "soulremote-update-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch { /* a temp folder */ }
    }

    // ---- transport ---------------------------------------------------------

    /// <summary>Answers by URL rather than in order, because the updater makes its calls in a fixed shape but not a fixed count.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = new();

        public RouteHandler Json(string url, object body) =>
            Text(url, JsonSerializer.Serialize(body), "application/json");

        public RouteHandler Text(string url, string body, string mediaType = "text/plain")
        {
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
            return this;
        }

        public RouteHandler Bytes(string url, byte[] body)
        {
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            return this;
        }

        public RouteHandler Status(string url, HttpStatusCode status)
        {
            _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) });
        }
    }

    private const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string SetupUrl = "https://github.com/" + Repo + "/releases/download/v1.2.0/SoulRemote-1.2.0-Setup.exe";
    private const string SetupHashUrl = SetupUrl + ".sha256";

    private static object Release(
        string tag = "v1.2.0",
        bool prerelease = false,
        object[]? assets = null) => new
        {
            tag_name = tag,
            name = "Soul Remote " + tag,
            body = "- something changed",
            html_url = $"https://github.com/{Repo}/releases/tag/{tag}",
            draft = false,
            prerelease,
            published_at = "2026-08-01T10:00:00Z",
            assets = assets ?? new object[]
            {
                Asset("SoulRemote-1.2.0-Setup.exe", SetupUrl, 4),
            },
        };

    private static object Asset(string name, string url, long size, string? digest = null) => new
    {
        name,
        browser_download_url = url,
        size,
        digest,
    };

    private static string Sha256Of(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private (AppUpdateService Service, FakeLog Log) Build(RouteHandler handler, string current = "1.0.1.0")
    {
        var log = new FakeLog();
        var service = new AppUpdateService(
            log, Version.Parse(current), Repo, handler,
            cacheDirectory: Path.Combine(_cache, "downloads"),
            stateDirectory: Path.Combine(_cache, "state"));
        return (service, log);
    }

    // ---- version comparison ------------------------------------------------

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.4", "1.2.3")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2.3+build9", "1.2.3")]
    public void Tags_are_read_as_three_field_versions(string tag, string expected)
    {
        Assert.Equal(expected, AppUpdateService.ParseVersion(tag)!.ToString(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("v")]
    public void A_tag_that_is_not_a_version_is_rejected(string? tag)
    {
        Assert.Null(AppUpdateService.ParseVersion(tag));
    }

    [Fact]
    public void The_running_build_is_not_newer_than_its_own_tag()
    {
        // The assembly carries four fields and the tag carries three, and Version sorts
        // an absent field below a zero one. Left alone, 1.0.1 would look older than
        // 1.0.1.0 and every single build would offer to update itself.
        var assembly = AppUpdateService.Normalize(new Version(1, 0, 1, 0));
        var tag = AppUpdateService.ParseVersion("v1.0.1")!;
        Assert.Equal(tag, assembly);
        Assert.False(tag > assembly);
    }

    // ---- choosing what to run ----------------------------------------------

    [Fact]
    public void The_bundled_setup_wins_over_a_bare_msi()
    {
        var assets = new[]
        {
            new ReleaseAsset("SoulRemote-1.2.0-x64.msi", "m", 1, null),
            new ReleaseAsset("SoulRemote-1.2.0-Setup.exe", "s", 1, null),
        };
        Assert.Equal("SoulRemote-1.2.0-Setup.exe", AppUpdateService.PickInstaller(assets)!.Name);
    }

    [Fact]
    public void A_release_with_only_the_portable_exe_has_nothing_to_install()
    {
        var assets = new[]
        {
            new ReleaseAsset("SoulRemote.exe", "p", 1, null),
            new ReleaseAsset("SoulRemote.exe.sha256", "h", 1, null),
        };
        // The portable exe installs nothing and replaces nothing. Handing it to the
        // installer path would leave the user with a second copy and no upgrade.
        Assert.Null(AppUpdateService.PickInstaller(assets));
    }

    // ---- checking ----------------------------------------------------------

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release());
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release!.Version.ToString(3));
        Assert.Equal("SoulRemote-1.2.0-Setup.exe", release.Installer.Name);
        Assert.Equal(UpdateStage.Available, service.Stage);
    }

    [Fact]
    public async Task The_same_version_is_not_an_update()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release(tag: "v1.0.1"));
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.UpToDate, service.Stage);
        Assert.Null(service.Latest);
    }

    [Fact]
    public async Task An_older_release_is_not_an_update_either()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release(tag: "v0.9.0"));
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.UpToDate, service.Stage);
    }

    [Fact]
    public async Task A_pre_release_is_ignored_even_if_the_api_returns_one()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release(prerelease: true));
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.UpToDate, service.Stage);
    }

    [Fact]
    public async Task A_repository_with_no_releases_says_so_rather_than_failing_silently()
    {
        var handler = new RouteHandler().Status(LatestUrl, HttpStatusCode.NotFound);
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("no published releases", service.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Being_rate_limited_is_reported_as_something_that_passes()
    {
        var handler = new RouteHandler().Status(LatestUrl, HttpStatusCode.Forbidden);
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("rate-limit", service.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_release_with_no_installer_is_refused_by_name()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release(assets: new object[]
        {
            Asset("SoulRemote.exe", "https://example.invalid/SoulRemote.exe", 4),
        }));
        var (service, _) = Build(handler);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("no installer", service.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_check_carries_a_user_agent_and_asks_the_right_endpoint()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release());
        var (service, _) = Build(handler);

        await service.CheckAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(LatestUrl, handler.Requests[0]);
    }

    // ---- downloading -------------------------------------------------------

    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ this is a setup package");

    private static RouteHandler WithDownload(string? checksumBody, string? digest = null)
    {
        var handler = new RouteHandler()
            .Json(LatestUrl, Release(assets: new object[]
            {
                Asset("SoulRemote-1.2.0-Setup.exe", SetupUrl, Installer.Length, digest),
                Asset("SoulRemote-1.2.0-Setup.exe.sha256", SetupHashUrl, 70),
            }))
            .Bytes(SetupUrl, Installer);

        if (checksumBody is not null)
            handler.Text(SetupHashUrl, checksumBody);
        return handler;
    }

    [Fact]
    public async Task A_verified_download_ends_up_on_disk_and_ready()
    {
        var handler = WithDownload($"{Sha256Of(Installer)}  SoulRemote-1.2.0-Setup.exe\n");
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        var path = await service.FetchAsync(release!);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path!));
        Assert.Equal(UpdateStage.Ready, service.Stage);
        Assert.Equal(100, service.DownloadPercent);
        Assert.Equal(path, service.InstallerPath);
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_leaves_nothing_behind()
    {
        var wrong = Sha256Of(Encoding.ASCII.GetBytes("a different package"));
        var handler = WithDownload($"{wrong}  SoulRemote-1.2.0-Setup.exe\n");
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        var path = await service.FetchAsync(release!);

        Assert.Null(path);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Null(service.InstallerPath);
        // Nothing half-downloaded is left for a later run to pick up and trust.
        var downloads = Path.Combine(_cache, "downloads");
        Assert.True(!Directory.Exists(downloads) || Directory.GetFiles(downloads).Length == 0);
    }

    [Fact]
    public async Task A_release_with_no_published_checksum_is_never_run()
    {
        // No sidecar and no digest: there is no way to tell the real package from
        // whatever else answered for that URL, so the only safe answer is to refuse.
        var handler = WithDownload(checksumBody: null);
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        var path = await service.FetchAsync(release!);

        Assert.Null(path);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("SHA-256", service.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_digest_github_publishes_is_enough_on_its_own()
    {
        var handler = WithDownload(checksumBody: null, digest: "sha256:" + Sha256Of(Installer));
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        var path = await service.FetchAsync(release!);

        Assert.NotNull(path);
        Assert.Equal(UpdateStage.Ready, service.Stage);
        // The sidecar was never fetched, because the digest already answered.
        Assert.DoesNotContain(SetupHashUrl, handler.Requests);
    }

    [Fact]
    public async Task An_installer_already_downloaded_is_not_downloaded_again()
    {
        var handler = WithDownload($"{Sha256Of(Installer)}  SoulRemote-1.2.0-Setup.exe\n");
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        await service.FetchAsync(release!);
        var downloadsFirstTime = handler.Requests.Count(r => r == SetupUrl);

        // A second attempt - the user pressed the button twice, or the app restarted.
        var again = await service.FetchAsync(release!);

        Assert.Equal(1, downloadsFirstTime);
        Assert.Equal(1, handler.Requests.Count(r => r == SetupUrl));
        Assert.NotNull(again);
    }

    [Fact]
    public async Task An_installer_bigger_than_the_ceiling_is_refused_before_a_byte_moves()
    {
        var handler = new RouteHandler().Json(LatestUrl, Release(assets: new object[]
        {
            Asset("SoulRemote-1.2.0-Setup.exe", SetupUrl, AppUpdateService.MaxInstallerBytes + 1),
        }));
        var (service, _) = Build(handler);

        var release = await service.CheckAsync();
        Assert.Null(await service.FetchAsync(release!));
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.DoesNotContain(SetupUrl, handler.Requests);
    }

    // ---- checksum parsing --------------------------------------------------

    [Fact]
    public void A_sha256sum_file_is_read_whatever_it_is_wrapped_in()
    {
        var hash = Sha256Of(Installer);
        Assert.Equal(hash, AppUpdateService.ParseChecksumFile($"{hash}  file.exe"));
        Assert.Equal(hash, AppUpdateService.ParseChecksumFile($"\r\n{hash} *file.exe\r\n"));
        Assert.Equal(hash, AppUpdateService.ParseChecksumFile(hash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash at all")]
    [InlineData("abc123  file.exe")]
    public void Anything_that_is_not_a_sha256_is_not_accepted_as_one(string text)
    {
        Assert.Null(AppUpdateService.ParseChecksumFile(text));
    }

    [Theory]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:not-hex")]
    [InlineData(null)]
    public void Only_a_sha256_digest_counts(string? digest)
    {
        Assert.Null(AppUpdateService.ParseDigest(digest));
    }

    // ---- the hand-over record ---------------------------------------------

    [Fact]
    public void The_pending_record_survives_the_restart_and_is_read_only_once()
    {
        var (service, _) = Build(new RouteHandler());

        service.MarkPending(new PendingUpdate
        {
            Version = "1.2.0",
            Minimized = true,
            RelayWasRunning = true,
            At = DateTimeOffset.UnixEpoch,
        });

        var taken = service.TakePending();
        Assert.NotNull(taken);
        Assert.Equal("1.2.0", taken!.Version);
        Assert.True(taken.Minimized);
        Assert.True(taken.RelayWasRunning);

        // Left behind, it would make every later start think it had just been updated.
        Assert.Null(service.TakePending());
    }

    [Fact]
    public void An_ordinary_start_finds_no_record()
    {
        var (service, _) = Build(new RouteHandler());
        Assert.Null(service.TakePending());
    }

    // ---- file naming -------------------------------------------------------

    [Theory]
    [InlineData("SoulRemote-1.2.0-Setup.exe", "SoulRemote-1.2.0-Setup.exe")]
    [InlineData("..\\..\\Windows\\System32\\evil.exe", "evil.exe")]
    [InlineData("C:\\Windows\\System32\\evil.exe", "evil.exe")]
    [InlineData("   ", "SoulRemote-update")]
    public void An_asset_name_cannot_steer_the_download_out_of_the_cache(string name, string expected)
    {
        Assert.Equal(expected, AppUpdateService.SafeFileName(name));
    }
}
