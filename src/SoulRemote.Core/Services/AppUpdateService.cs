using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Finds newer releases of Soul Remote on GitHub and puts a verified installer on
/// disk. It deliberately stops there: starting the installer means replacing the
/// running executable and closing the app, which is Windows' business and lives in
/// the desktop project. Everything up to that point — the API call, the version
/// comparison, choosing an asset, the checksum — is here so it can be tested without
/// a network and without Windows.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>The build that is running, always as three fields.</summary>
    Version CurrentVersion { get; }

    UpdateStage Stage { get; }

    /// <summary>The newer release, once a check has found one. Null at every other stage.</summary>
    AppRelease? Latest { get; }

    /// <summary>Something short and true to put on screen: the reason for a failure, or the new version.</summary>
    string Message { get; }

    /// <summary>The verified installer on disk, once the stage is <see cref="UpdateStage.Ready"/>.</summary>
    string? InstallerPath { get; }

    /// <summary>0-100 while downloading.</summary>
    int DownloadPercent { get; }

    /// <summary>Raised on any change to the properties above, on whichever thread made it.</summary>
    event Action? Changed;

    /// <summary>Asks GitHub for the latest release. Returns it only when it is newer than this build.</summary>
    Task<AppRelease?> CheckAsync(CancellationToken ct = default);

    /// <summary>Downloads the release's installer and verifies its SHA-256. Returns the path, or null.</summary>
    Task<string?> FetchAsync(AppRelease release, CancellationToken ct = default);

    /// <summary>Records that this copy is handing itself to an installer.</summary>
    void MarkPending(PendingUpdate pending);

    /// <summary>Reads and clears the record written before an update. Null on an ordinary start.</summary>
    PendingUpdate? TakePending();

    /// <summary>Where verified installers are kept.</summary>
    string CacheDirectory { get; }
}

public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    /// <summary>The repository releases are published from.</summary>
    public const string DefaultRepository = "mrsoulcommunity/SoulRemote";

    /// <summary>
    /// A ceiling on what we will pull down. The real installer is around 60 MB; this is
    /// wide enough for it to grow and narrow enough that a wrong or hostile URL cannot
    /// fill the disk before the checksum gets a chance to reject it.
    /// </summary>
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    private const string PendingFileName = "update.pending";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Version CurrentVersion { get; }
    public string CacheDirectory { get; }

    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    public AppRelease? Latest { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? InstallerPath { get; private set; }
    public int DownloadPercent { get; private set; }

    public event Action? Changed;

    /// <param name="currentVersion">
    /// The running build. Anything past the third field is ignored. Nullable because
    /// that is what Assembly.GetName().Version is declared as; a null one is read as
    /// 0.0.0, which makes every published release newer rather than none of them.
    /// </param>
    /// <param name="handler">
    /// Transport override. Production leaves this null; the tests hand in a stub so the
    /// whole cycle — a release with no installer, a checksum that does not match, a
    /// rate-limited API — can be exercised without a network.
    /// </param>
    public AppUpdateService(
        ILogService log,
        Version? currentVersion,
        string? repository = null,
        HttpMessageHandler? handler = null,
        string? cacheDirectory = null,
        string? stateDirectory = null)
    {
        _log = log;
        CurrentVersion = Normalize(currentVersion);
        _repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim().Trim('/');

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // Generous, because this competes with a long-poll on the same link and the
        // download is tens of megabytes. Cancellation does the fine control.
        _http.Timeout = TimeSpan.FromMinutes(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SoulRemote/{CurrentVersion.ToString(3)}");

        _stateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulRemote");
        CacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "SoulRemote", "updates");
    }

    // ---- checking ----------------------------------------------------------

    public async Task<AppRelease?> CheckAsync(CancellationToken ct = default)
    {
        // A check already running is the answer to a second one; queueing them would
        // only spend the hourly API allowance twice as fast.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return Latest;

        try
        {
            Report(UpdateStage.Checking, string.Empty);

            var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (release is null)
                return null;

            var version = ParseVersion(release.TagName) ?? ParseVersion(release.Name);
            if (version is null)
            {
                Fail($"The latest release is tagged '{release.TagName}', which is not a version number.");
                return null;
            }

            if (version <= CurrentVersion)
            {
                Latest = null;
                InstallerPath = null;
                Report(UpdateStage.UpToDate, CurrentVersion.ToString(3));
                _log.Debug($"Update check: {CurrentVersion.ToString(3)} is current (latest published is {version.ToString(3)}).");
                return null;
            }

            var assets = release.Assets
                .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!, a.Size, a.Digest))
                .ToList();

            var installer = PickInstaller(assets);
            if (installer is null)
            {
                Fail($"Release {version.ToString(3)} attaches no installer, so it cannot be applied automatically.");
                return null;
            }

            var checksum = assets.FirstOrDefault(a =>
                a.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            Latest = new AppRelease(
                version,
                release.TagName ?? version.ToString(3),
                release.Body ?? string.Empty,
                string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? $"https://github.com/{_repository}/releases"
                    : release.HtmlUrl!,
                release.PublishedAt,
                installer,
                checksum);

            InstallerPath = null;
            Report(UpdateStage.Available, version.ToString(3));
            _log.Info($"Update available: {version.ToString(3)} (running {CurrentVersion.ToString(3)}).");
            return Latest;
        }
        catch (OperationCanceledException)
        {
            Report(UpdateStage.Idle, string.Empty);
            throw;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            _log.Debug($"Update check failed: {ex}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GhRelease?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Fail(DescribeApiFailure(response));
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GhRelease>(json, JsonOptions);
        if (release is null)
        {
            Fail("GitHub returned a release that could not be read.");
            return null;
        }

        // The "latest" endpoint already filters these out; a repository that has only
        // ever published pre-releases answers 404 instead. Checked anyway, so a change
        // at the other end cannot push a draft onto every installed copy.
        if (release.Draft || release.Prerelease)
        {
            Latest = null;
            Report(UpdateStage.UpToDate, CurrentVersion.ToString(3));
            return null;
        }

        return release;
    }

    private static string DescribeApiFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.NotFound =>
            "There are no published releases to update from yet.",
        HttpStatusCode.Forbidden or (HttpStatusCode)429 =>
            "GitHub is rate-limiting update checks from this network. It will answer again within the hour.",
        _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.",
    };

    // ---- downloading -------------------------------------------------------

    public async Task<string?> FetchAsync(AppRelease release, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return InstallerPath;

        try
        {
            if (release.Installer.Size > MaxInstallerBytes)
            {
                Fail($"The installer for {release.Version.ToString(3)} is larger than this app will download.");
                return null;
            }

            DownloadPercent = 0;
            Report(UpdateStage.Downloading, release.Version.ToString(3));

            var expected = await ResolveExpectedHashAsync(release, ct).ConfigureAwait(false);
            if (expected is null)
            {
                // Running an unverified installer would hand this machine to whoever
                // could answer for the download. Refusing is the only safe answer, and
                // the release page is still one click away on the Settings page.
                Fail($"Release {release.Version.ToString(3)} publishes no SHA-256, so its installer cannot be verified.");
                return null;
            }

            Directory.CreateDirectory(CacheDirectory);
            var target = Path.Combine(CacheDirectory, SafeFileName(release.Installer.Name));

            // A download finished by an earlier attempt is worth re-checking before
            // pulling sixty megabytes over the same link again.
            if (File.Exists(target) && ComputeSha256(target) == expected)
            {
                _log.Info($"Update {release.Version.ToString(3)} was already downloaded and still verifies.");
                return Ready(target, release);
            }

            var partial = target + ".part";
            try
            {
                await DownloadToFileAsync(release.Installer.Url, partial, release.Installer.Size, ct)
                    .ConfigureAwait(false);

                var actual = ComputeSha256(partial);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partial);
                    Fail("The downloaded installer did not match the published SHA-256, so it was discarded.");
                    _log.Error($"Update {release.Version.ToString(3)}: expected {expected}, got {actual}.");
                    return null;
                }

                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            PruneCache(target);
            _log.Info($"Update {release.Version.ToString(3)} downloaded and verified.");
            return Ready(target, release);
        }
        catch (OperationCanceledException)
        {
            Report(UpdateStage.Available, release.Version.ToString(3));
            throw;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            _log.Debug($"Update download failed: {ex}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Ready(string path, AppRelease release)
    {
        InstallerPath = path;
        DownloadPercent = 100;
        Report(UpdateStage.Ready, release.Version.ToString(3));
        return path;
    }

    private async Task<string?> ResolveExpectedHashAsync(AppRelease release, CancellationToken ct)
    {
        if (ParseDigest(release.Installer.Digest) is { } digest)
            return digest;

        if (release.Checksum is null)
            return null;

        using var response = await _http.GetAsync(release.Checksum.Url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseChecksumFile(text);
    }

    private async Task DownloadToFileAsync(string url, string path, long declaredSize, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? declaredSize;
        if (total > MaxInstallerBytes)
            throw new InvalidOperationException("The installer is larger than this app will download.");

        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long written = 0;
        var lastReported = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            written += read;
            // Content-Length can be absent or untrue; the cap is enforced against what
            // actually arrives, not against what the server said it would send.
            if (written > MaxInstallerBytes)
                throw new InvalidOperationException("The download exceeded the size this app will accept.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            if (total <= 0)
                continue;
            var percent = (int)Math.Clamp(written * 100 / total, 0, 100);
            if (percent == lastReported)
                continue;
            lastReported = percent;
            DownloadPercent = percent;
            Changed?.Invoke();
        }
    }

    /// <summary>Clears everything else out of the cache; only the newest installer is worth keeping.</summary>
    private void PruneCache(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not tidy the update cache: {ex.Message}");
        }
    }

    // ---- the hand-over record ---------------------------------------------

    private string PendingPath => Path.Combine(_stateDirectory, PendingFileName);

    public void MarkPending(PendingUpdate pending)
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, JsonOptions));
        }
        catch (Exception ex)
        {
            // Losing this costs the new copy its window state, not the update itself.
            _log.Warning($"Could not record the pending update: {ex.Message}");
        }
    }

    public PendingUpdate? TakePending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            var json = File.ReadAllText(PendingPath);
            // Cleared whether or not it parses: a record that survived would re-apply
            // itself to every start from here on.
            TryDelete(PendingPath);
            return JsonSerializer.Deserialize<PendingUpdate>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read the pending update record: {ex.Message}");
            TryDelete(PendingPath);
            return null;
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Reduces a version to the three fields Windows Installer compares. The app's
    /// assembly version carries a fourth ("1.0.1.0") and a tag does not ("v1.0.1"), and
    /// <see cref="Version"/> sorts an absent field below a zero one — so without this,
    /// 1.0.1 would look older than 1.0.1.0 and every build would offer to update itself.
    /// </summary>
    internal static Version Normalize(Version? version) =>
        version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>Reads "v1.2.3", "1.2.3" or "1.2.3-beta.1" as a version. Null when it is not one.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var s = text.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        // Everything from a pre-release or build-metadata marker onwards is not part of
        // the number. Ordering those properly is semver's problem, and this app does not
        // publish them.
        var cut = s.IndexOfAny(new[] { '-', '+', ' ', '_' });
        if (cut >= 0)
            s = s[..cut];

        return Version.TryParse(s, out var parsed) ? Normalize(parsed) : null;
    }

    /// <summary>
    /// Chooses what to run. The bundled setup.exe first — it carries the MSI and can be
    /// driven silently — then a bare .msi, so a release published before the bundle
    /// existed still upgrades. Nothing else on a release may be handed to Windows: the
    /// portable SoulRemote.exe would install nothing and replace nothing.
    /// </summary>
    internal static ReleaseAsset? PickInstaller(IReadOnlyList<ReleaseAsset> assets)
    {
        return Best(a => a.Name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase))
            ?? Best(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

        ReleaseAsset? Best(Func<ReleaseAsset, bool> match)
        {
            var matches = assets.Where(match).ToList();
            if (matches.Count == 0)
                return null;
            // Only x64 is published today; if that ever stops being true, this build is
            // x64 and must not pick up somebody else's architecture.
            return matches.FirstOrDefault(a => a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                   ?? matches[0];
        }
    }

    /// <summary>Reads the hash out of a sha256sum-style file. Null when there is not one in it.</summary>
    internal static string? ParseChecksumFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var line in text.Split('\n'))
        {
            var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (IsSha256Hex(token))
                return token!.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Reads GitHub's own "sha256:hex" asset digest.</summary>
    internal static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        value = value[prefix.Length..];
        return IsSha256Hex(value) ? value.ToLowerInvariant() : null;
    }

    private static bool IsSha256Hex(string? token) =>
        token is { Length: 64 } && token.All(Uri.IsHexDigit);

    /// <summary>Strips anything that could steer the download out of the cache folder.</summary>
    internal static string SafeFileName(string name)
    {
        var trimmed = Path.GetFileName(name.Trim());
        foreach (var bad in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(bad, '_');
        return string.IsNullOrWhiteSpace(trimmed) ? "SoulRemote-update" : trimmed;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leftover in the temp folder is not worth reporting */ }
    }

    private void Fail(string message)
    {
        Latest = null;
        InstallerPath = null;
        Report(UpdateStage.Failed, message);
        _log.Warning($"Update: {message}");
    }

    private void Report(UpdateStage stage, string message)
    {
        Stage = stage;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
