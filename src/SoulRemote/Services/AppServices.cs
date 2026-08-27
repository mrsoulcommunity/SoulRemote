using System.Reflection;
using SoulRemote.Abstractions;
using SoulRemote.Localization;
using SoulRemote.Platform;

namespace SoulRemote.Services;

/// <summary>
/// Tiny composition root. Constructs the service graph once and exposes it to
/// the view models. Kept manual to avoid a DI container dependency.
///
/// This is also where the two halves of the app meet: everything platform-neutral
/// comes from SoulRemote.Core, and the Windows implementations of its interfaces —
/// the dispatcher, DPAPI, screen capture, system control — are supplied here.
/// </summary>
public sealed class AppServices
{
    public ILogService Log { get; }
    public ISettingsService Settings { get; }
    public ICloudflareService Cloudflare { get; }
    public ITelegramClient Telegram { get; }

    /// <summary>
    /// The premium-emoji look. Public because both settings surfaces report on it —
    /// the desktop card and the bot's own screen — and neither can say whether
    /// Telegram is honouring the emoji without asking it.
    /// </summary>
    public PremiumEmojiStyler Emoji { get; }
    public ISystemControlService System { get; }
    public IScreenshotService Screenshot { get; }
    public ISystemInfoService Info { get; }
    public IStartupManager Startup { get; }
    public IPcSettingsService PcSettings { get; }
    public IAppUpdateService Updates { get; }
    public IUpdateInstaller Installer { get; }
    public UpdateCoordinator UpdateCoordinator { get; }
    public CommandRouter Router { get; }
    public BotEngine Bot { get; }
    public ConnectionOrchestrator Orchestrator { get; }

    public AppServices()
    {
        Log = new LogService(WpfDispatcher.Instance);
        Settings = new SettingsService(Log, DpapiSecretProtector.Instance);
        var settings = Settings.Load();

        // The language has to be live before anything renders a string.
        Strings.Use(settings.LanguageOrDefault);

        // Old log files are swept once, at startup: the app is designed to run for
        // weeks at a time, so nothing else would ever get round to it.
        var pruned = Log.PruneOlderThan(settings.LogRetentionDays);
        if (pruned > 0)
            Log.Info($"Removed {pruned} log file(s) older than {settings.LogRetentionDays} days.");

        Cloudflare = new CloudflareService(Log);

        // Built before the client and handed to it: the emoji are applied on the way
        // out, at the one point every message passes through, rather than at each of
        // the places the bot composes one.
        Emoji = new PremiumEmojiStyler(Settings, Log);
        Telegram = new TelegramClient(Log, handler: null, emoji: Emoji);
        System = new SystemControlService(Log);
        Screenshot = new ScreenshotService();
        Info = new SystemInfoService(Log, Settings, Cloudflare);
        Startup = new StartupManager(Log);
        PcSettings = new PcSettingsService(Log);

        // The updater is given the assembly version rather than a constant, so the
        // number it compares against GitHub is the one the build actually stamped.
        Updates = new AppUpdateService(Log, Assembly.GetExecutingAssembly().GetName().Version);
        Installer = new UpdateInstaller(Log);
        UpdateCoordinator = new UpdateCoordinator(Updates, Installer, Settings, Log);

        // The router is handed the startup manager and the Windows-settings service
        // as well, because the bot's Settings section writes through both.
        Router = new CommandRouter(Settings, Telegram, System, Screenshot, Info, Log,
            clock: null, pc: PcSettings, startup: Startup, emoji: Emoji);
        Bot = new BotEngine(Settings, Telegram, Router, Log);
        Orchestrator = new ConnectionOrchestrator(Settings, Cloudflare, Telegram, Bot, Log, WpfDispatcher.Instance);
    }
}
