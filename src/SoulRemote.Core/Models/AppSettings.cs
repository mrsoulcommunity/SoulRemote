using System.Text.Json.Serialization;
using SoulRemote.Localization;

namespace SoulRemote.Models;

/// <summary>
/// Persisted application configuration. Secret values (API tokens, bot token)
/// are stored on disk encrypted with the Windows Data Protection API (DPAPI),
/// scoped to the current user. The plaintext properties here are the in-memory
/// working copy; the settings service handles encrypt-on-save / decrypt-on-load.
/// </summary>
public sealed class AppSettings
{
    // ---- Cloudflare ----
    public string CloudflareApiToken { get; set; } = string.Empty;
    public string CloudflareAccountId { get; set; } = string.Empty;
    public string CloudflareAccountName { get; set; } = string.Empty;

    /// <summary>Worker script name (lowercase, dashes). Deployed to workers.dev.</summary>
    public string WorkerName { get; set; } = "soul-remote-proxy";

    /// <summary>The workers.dev subdomain of the account, e.g. "myname".</summary>
    public string WorkersDevSubdomain { get; set; } = string.Empty;

    /// <summary>Full public worker URL, e.g. https://soul-remote-proxy.myname.workers.dev.</summary>
    public string WorkerUrl { get; set; } = string.Empty;

    /// <summary>Shared secret sent as X-Proxy-Secret so the worker isn't an open relay.</summary>
    public string ProxySecret { get; set; } = string.Empty;

    // ---- Telegram ----
    public string TelegramBotToken { get; set; } = string.Empty;
    public string TelegramBotUsername { get; set; } = string.Empty;

    /// <summary>Chat IDs allowed to control this machine.</summary>
    public List<long> AuthorizedChatIds { get; set; } = new();

    /// <summary>
    /// Display names for the paired chats, keyed by chat id as a string so the JSON
    /// stays readable. Purely cosmetic: the whitelist above is what grants access, and
    /// a chat with no name here is still authorized. It exists because a dashboard
    /// listing "6291445123  ·  884120993" tells the owner nothing about which of their
    /// devices they would be revoking.
    /// </summary>
    public Dictionary<string, string> ChatNames { get; set; } = new();

    // ---- Behaviour ----
    /// <summary>Master switch: allow arbitrary shell command execution via /cmd.</summary>
    public bool AllowShellCommands { get; set; } = false;

    /// <summary>Allow browsing folders, fetching files and opening local paths.</summary>
    public bool AllowFileAccess { get; set; } = false;

    /// <summary>
    /// Allow typing into the focused window. Off by default, in line with the other
    /// two: synthetic keystrokes plus a focused terminal reach the same place /cmd
    /// does, so leaving this open while /cmd is described as the master switch for
    /// running commands would make that description untrue.
    /// </summary>
    public bool AllowInputInjection { get; set; } = false;

    /// <summary>
    /// Whether a paired chat may change settings from Telegram, or only use them.
    /// On by default, because a bot you cannot configure from the phone you are
    /// holding is the case this app exists for. Turning it off is the way back: it
    /// is the one switch the bot cannot reach, so a chat that has been taken over
    /// cannot undo it, and the desktop owner always has the last word.
    /// </summary>
    public bool AllowRemoteSettings { get; set; } = true;

    /// <summary>Launch Soul Remote automatically when the user signs in.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Start the bot engine automatically when the app launches.</summary>
    public bool AutoStartBot { get; set; } = false;

    /// <summary>Start minimized to the system tray.</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>Send a Telegram message to authorized users when the bot comes online.</summary>
    public bool NotifyOnStartup { get; set; } = true;

    /// <summary>Long-poll timeout (seconds) for Telegram getUpdates.</summary>
    public int PollTimeoutSeconds { get; set; } = 25;

    /// <summary>Stops the relay-line animation for users who prefer a still interface.</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>UI and bot language, stored as a two-letter tag ("en" / "fa").</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Look for a newer release on GitHub at startup and once a day after that. The
    /// check is one unauthenticated request and sends nothing about this machine.
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// Download and apply a newer release without asking first. Off by default: being
    /// told what is about to happen, once, and pressing one button is the flow this app
    /// is built around. Turning it on suits a machine nobody signs in to. Either way the
    /// installer is only ever run when its published SHA-256 matched, and only against a
    /// copy that an installer put there in the first place.
    /// </summary>
    public bool AutoInstallUpdates { get; set; } = false;

    /// <summary>Log files older than this are deleted at startup. 0 keeps them forever.</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Where files received from Telegram are written. Empty means the default Downloads folder.</summary>
    public string DownloadFolder { get; set; } = string.Empty;

    // ---- Premium emoji ----

    /// <summary>
    /// Whether the bot dresses its messages in the custom emoji mapped below. Kept
    /// separate from the map so turning the look off, and back on, does not mean
    /// setting every emoji up again.
    /// </summary>
    public bool UsePremiumEmoji { get; set; } = true;

    /// <summary>
    /// Which Telegram custom ("premium") emoji stands in for each of the bot's own,
    /// keyed by the ordinary emoji and holding the custom emoji's identifier:
    /// <c>{ "📸": "5368324170671202286" }</c>.
    ///
    /// Keyed by the character rather than by a name for the screen it appears on,
    /// because that is what the substitution actually matches, and because it is the
    /// one key that cannot go stale: rename a screen, move an emoji to another menu,
    /// and the mapping still lands. Telegram also requires the entity to wrap exactly
    /// the emoji the custom one stands for, so the key is what makes an entry valid
    /// as well as what finds it.
    ///
    /// Lives here, in %APPDATA%\SoulRemote\settings.json, so it travels with the
    /// roaming profile and is one save away from both the desktop window and the bot.
    /// </summary>
    public Dictionary<string, string> PremiumEmoji { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The emoji pack the map was last filled from, e.g. "MyAnimatedEmoji". Purely a
    /// label — the identifiers above are what the bot sends — but it is what lets both
    /// settings screens say where the current look came from, and re-import it later
    /// without asking the user to find the pack again.
    /// </summary>
    public string PremiumEmojiPack { get; set; } = string.Empty;

    [JsonIgnore]
    public AppLanguage LanguageOrDefault => AppLanguageExtensions.Parse(Language);

    /// <summary>The remembered name for a chat, or its id when there is none.</summary>
    public string NameFor(long chatId)
    {
        var key = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ChatNames.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name) ? name : key;
    }

    [JsonIgnore]
    public bool HasCloudflare => !string.IsNullOrWhiteSpace(CloudflareApiToken) && !string.IsNullOrWhiteSpace(WorkerUrl);

    [JsonIgnore]
    public bool HasTelegram => !string.IsNullOrWhiteSpace(TelegramBotToken);

    /// <summary>
    /// Brings hand-edited or legacy files back into range. Called on both load and save
    /// so the same rules apply whichever direction the value came from.
    /// </summary>
    public void Normalize()
    {
        PollTimeoutSeconds = Math.Clamp(PollTimeoutSeconds <= 0 ? 25 : PollTimeoutSeconds, 5, 50);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 0, 365);
        Language = LanguageOrDefault.Tag();
        if (string.IsNullOrWhiteSpace(WorkerName))
            WorkerName = "soul-remote-proxy";
        AuthorizedChatIds = AuthorizedChatIds.Distinct().ToList();

        // Names for chats that are no longer paired are dead weight, and keeping them
        // would quietly re-label a chat id that later belonged to someone else.
        var live = AuthorizedChatIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        foreach (var stale in ChatNames.Keys.Where(k => !live.Contains(k)).ToArray())
            ChatNames.Remove(stale);

        NormalizePremiumEmoji();
    }

    /// <summary>
    /// Throws out emoji mappings Telegram would refuse.
    ///
    /// Only the identifier is judged, never the emoji it is filed under. An id that
    /// is not a number is an error every time it is sent, so it goes; but an emoji
    /// this build happens not to use any more is merely inert, and a release that
    /// reworded one string is no reason to quietly delete part of an imported pack
    /// the user may still be using on the version they roll back to.
    /// </summary>
    private void NormalizePremiumEmoji()
    {
        // A file holding "premiumEmoji": null deserialises to a null dictionary; the
        // rest of the app is entitled to assume there is always one to read.
        PremiumEmoji ??= new Dictionary<string, string>(StringComparer.Ordinal);
        PremiumEmojiPack = (PremiumEmojiPack ?? string.Empty).Trim();

        var cleaned = new Dictionary<string, string>(PremiumEmoji.Count, StringComparer.Ordinal);
        foreach (var (emoji, id) in PremiumEmoji)
        {
            if (!string.IsNullOrEmpty(emoji) && Services.EmojiText.IsValidCustomEmojiId(id))
                cleaned[emoji] = id.Trim();
        }
        PremiumEmoji = cleaned;
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            CloudflareApiToken = CloudflareApiToken,
            CloudflareAccountId = CloudflareAccountId,
            CloudflareAccountName = CloudflareAccountName,
            WorkerName = WorkerName,
            WorkersDevSubdomain = WorkersDevSubdomain,
            WorkerUrl = WorkerUrl,
            ProxySecret = ProxySecret,
            TelegramBotToken = TelegramBotToken,
            TelegramBotUsername = TelegramBotUsername,
            AuthorizedChatIds = new List<long>(AuthorizedChatIds),
            ChatNames = new Dictionary<string, string>(ChatNames),
            AllowShellCommands = AllowShellCommands,
            AllowFileAccess = AllowFileAccess,
            AllowInputInjection = AllowInputInjection,
            AllowRemoteSettings = AllowRemoteSettings,
            StartWithWindows = StartWithWindows,
            AutoStartBot = AutoStartBot,
            StartMinimized = StartMinimized,
            NotifyOnStartup = NotifyOnStartup,
            PollTimeoutSeconds = PollTimeoutSeconds,
            ReduceMotion = ReduceMotion,
            Language = Language,
            AutoCheckUpdates = AutoCheckUpdates,
            AutoInstallUpdates = AutoInstallUpdates,
            LogRetentionDays = LogRetentionDays,
            DownloadFolder = DownloadFolder,
            UsePremiumEmoji = UsePremiumEmoji,
            PremiumEmoji = new Dictionary<string, string>(PremiumEmoji, StringComparer.Ordinal),
            PremiumEmojiPack = PremiumEmojiPack,
        };
    }
}
