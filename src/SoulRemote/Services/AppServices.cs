namespace SoulRemote.Services;

/// <summary>
/// Tiny composition root. Constructs the service graph once and exposes it to
/// the view models. Kept manual to avoid a DI container dependency.
/// </summary>
public sealed class AppServices
{
    public ILogService Log { get; }
    public ISettingsService Settings { get; }
    public ICloudflareService Cloudflare { get; }
    public ITelegramClient Telegram { get; }
    public ISystemControlService System { get; }
    public IScreenshotService Screenshot { get; }
    public ISystemInfoService Info { get; }
    public IStartupManager Startup { get; }
    public CommandRouter Router { get; }
    public BotEngine Bot { get; }

    public AppServices()
    {
        Log = new LogService();
        Settings = new SettingsService(Log);
        Settings.Load();

        Cloudflare = new CloudflareService(Log);
        Telegram = new TelegramClient(Log);
        System = new SystemControlService(Log);
        Screenshot = new ScreenshotService();
        Info = new SystemInfoService(Log);
        Startup = new StartupManager(Log);

        Router = new CommandRouter(Settings, Telegram, System, Screenshot, Info, Log);
        Bot = new BotEngine(Settings, Telegram, Router, Log);
    }
}
