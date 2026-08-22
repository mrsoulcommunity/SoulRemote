using System.Text.Json.Serialization;

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

    // ---- Behaviour ----
    /// <summary>Master switch: allow arbitrary shell command execution via /cmd.</summary>
    public bool AllowShellCommands { get; set; } = false;

    /// <summary>Allow file browsing / download commands.</summary>
    public bool AllowFileAccess { get; set; } = false;

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

    [JsonIgnore]
    public bool HasCloudflare => !string.IsNullOrWhiteSpace(CloudflareApiToken) && !string.IsNullOrWhiteSpace(WorkerUrl);

    [JsonIgnore]
    public bool HasTelegram => !string.IsNullOrWhiteSpace(TelegramBotToken);

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
            AllowShellCommands = AllowShellCommands,
            AllowFileAccess = AllowFileAccess,
            StartWithWindows = StartWithWindows,
            AutoStartBot = AutoStartBot,
            StartMinimized = StartMinimized,
            NotifyOnStartup = NotifyOnStartup,
            PollTimeoutSeconds = PollTimeoutSeconds,
        };
    }
}
