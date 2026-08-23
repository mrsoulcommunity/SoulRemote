using System.Text.Json.Serialization;

namespace SoulRemote.Models;

// The subset of the GitHub Releases API that Soul Remote reads. Only the "latest
// release" endpoint is used, and it already excludes drafts and pre-releases:
// https://docs.github.com/rest/releases/releases#get-the-latest-release

public sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = new();
}

public sealed class GhAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>
    /// GitHub's own digest for the upload, formatted "sha256:hex". It is not present on
    /// releases published before GitHub started recording it, which is why the build
    /// also uploads a .sha256 file next to each artefact.
    /// </summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>One downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

/// <summary>
/// A release that is newer than the running build, together with the installer that
/// would apply it. <see cref="Checksum"/> is the .sha256 sidecar when the release has
/// one; without either that or <see cref="ReleaseAsset.Digest"/> the download cannot
/// be verified and is refused.
/// </summary>
public sealed record AppRelease(
    Version Version,
    string Tag,
    string Notes,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    ReleaseAsset Installer,
    ReleaseAsset? Checksum);

/// <summary>Where the updater is in its cycle. Drives the wording on the Settings page.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been asked of it yet.</summary>
    Idle,
    Checking,
    /// <summary>The latest release is the one already running.</summary>
    UpToDate,
    /// <summary>A newer release exists and has an installer we could fetch.</summary>
    Available,
    Downloading,
    /// <summary>The installer is on disk and its checksum matched.</summary>
    Ready,
    /// <summary>The installer has been handed to Windows; the app is about to exit.</summary>
    Installing,
    Failed,
}

/// <summary>
/// Written just before the app hands itself to the installer, and read back by the
/// copy the installer starts. Without it the new build has no way of knowing it was
/// mid-upgrade: it would show a window on a machine nobody is sitting at, and it would
/// leave the relay down on a machine whose whole purpose is to be reachable.
/// </summary>
public sealed class PendingUpdate
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    /// <summary>The window was hidden in the tray, so the new copy should start there too.</summary>
    [JsonPropertyName("minimized")] public bool Minimized { get; set; }

    /// <summary>The relay was running, so the new copy should bring it back up.</summary>
    [JsonPropertyName("relayWasRunning")] public bool RelayWasRunning { get; set; }

    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
}
