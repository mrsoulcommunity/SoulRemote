using System.Collections.Concurrent;
using System.IO;
using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>One row in a folder listing.</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long Size);

/// <summary>A rendered folder: the text to show and the entries the buttons refer to.</summary>
public sealed record FolderListing(string Path, string? Parent, IReadOnlyList<FileEntry> Entries, bool Truncated);

/// <summary>
/// Browsing and fetching files on this PC, for the chats allowed to do it.
///
/// Telegram caps callback_data at 64 bytes, which no real Windows path fits in, so
/// buttons carry an index into the listing this chat is currently looking at rather
/// than a path. That also means a stale button — a tap on a listing from ten minutes
/// ago — resolves against the listing that produced it or not at all, instead of
/// against whatever folder happens to be current.
/// </summary>
public sealed class FileBrowser
{
    /// <summary>Rows per screen. Telegram's keyboards get unusable long before this.</summary>
    public const int PageSize = 20;

    private readonly ConcurrentDictionary<long, FolderListing> _cursors = new();

    /// <summary>The folder a chat is currently looking at, if any.</summary>
    public FolderListing? CurrentFor(long chatId) =>
        _cursors.TryGetValue(chatId, out var listing) ? listing : null;

    public void Forget(long chatId) => _cursors.TryRemove(chatId, out _);

    /// <summary>
    /// Lists a folder and remembers it as this chat's position. Throws with a
    /// user-readable message rather than a raw .NET exception, because the message
    /// goes straight into the chat.
    /// </summary>
    public FolderListing List(long chatId, string path)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
            throw new InvalidOperationException(Strings.Get("bot.files.notfound"));

        var entries = new List<FileEntry>();
        var truncated = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (entries.Count >= PageSize) { truncated = true; break; }
                entries.Add(new FileEntry(Path.GetFileName(dir), dir, true, 0));
            }
            if (!truncated)
            {
                foreach (var file in Directory.EnumerateFiles(full).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (entries.Count >= PageSize) { truncated = true; break; }
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { /* unreadable: still list it */ }
                    entries.Add(new FileEntry(Path.GetFileName(file), file, false, size));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(Strings.Get("bot.files.denied"));
        }

        var listing = new FolderListing(full, ParentOf(full), entries, truncated);
        _cursors[chatId] = listing;
        return listing;
    }

    /// <summary>Reads a file for sending, refusing anything Telegram would reject anyway.</summary>
    public byte[] Read(string path, long maxBytes)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
            throw new InvalidOperationException(Strings.Get("bot.files.notfound"));

        var info = new FileInfo(full);
        if (info.Length > maxBytes)
            throw new InvalidOperationException(Strings.Format("bot.files.toobig",
                TextUtil.HumanBytes(info.Length), TextUtil.HumanBytes(maxBytes)));

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(Strings.Get("bot.files.denied"));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    /// <summary>
    /// Writes a file that arrived from Telegram, into the configured download folder.
    /// The sender chooses the name, so it is reduced to a bare file name and any
    /// collision is given a numeric suffix — an incoming file must never be able to
    /// pick its own directory or quietly replace something already there.
    /// </summary>
    public string Save(string? suggestedName, byte[] content, string folder)
    {
        Directory.CreateDirectory(folder);

        var name = Path.GetFileName(suggestedName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            name = "file";
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        var target = Path.Combine(folder, name);
        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var i = 2; File.Exists(target); i++)
                target = Path.Combine(folder, $"{stem} ({i}){ext}");
        }

        File.WriteAllBytes(target, content);
        return target;
    }

    /// <summary>The default place received files land when settings name none.</summary>
    public static string DefaultDownloadFolder(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DownloadFolder))
            return settings.DownloadFolder;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(profile) ? Path.GetTempPath() : profile,
            "Downloads", "Soul Remote");
    }

    /// <summary>Renders a listing as the panel text.</summary>
    public static string Describe(FolderListing listing)
    {
        var text = Strings.Format("bot.files.listing", TextUtil.Html(listing.Path));
        if (listing.Entries.Count == 0)
            return text + "\n" + Strings.Get("bot.files.empty");
        if (listing.Truncated)
            text += "\n" + Strings.Format("bot.files.truncated", PageSize);
        return text;
    }

    /// <summary>The parent folder, or null at a drive root.</summary>
    private static string? ParentOf(string path)
    {
        try
        {
            var parent = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return parent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string Resolve(string path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
            throw new InvalidOperationException(Strings.Get("bot.files.notfound"));
        try
        {
            // Collapses "..", resolves a relative path, and rejects the genuinely malformed.
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(Strings.Get("bot.files.notfound"));
        }
    }
}
