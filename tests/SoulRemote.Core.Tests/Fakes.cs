using System.Collections.ObjectModel;
using SoulRemote.Abstractions;
using SoulRemote.Models;
using SoulRemote.Services;

namespace SoulRemote.Tests;

/// <summary>A clock the test moves by hand, so nothing has to sleep.</summary>
public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeLog : ILogService
{
    private readonly ObservableCollection<LogEntry> _entries = new();

    public FakeLog() => Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }
    public string LogDirectory => Path.GetTempPath();
    public List<string> Messages { get; } = new();

    public void Log(LogLevel level, string message)
    {
        Messages.Add($"{level}: {message}");
        _entries.Add(new LogEntry { Level = level, Message = message });
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Log(LogLevel.Error, $"{message}: {ex.Message}");
    public void Clear() => _entries.Clear();
    public int PruneOlderThan(int days) => 0;
    public int DeleteAllFiles() => 0;
}

/// <summary>Settings held in memory, with a switch to make saving fail.</summary>
public sealed class FakeSettings : ISettingsService
{
    public FakeSettings(AppSettings? initial = null) => Current = initial ?? new AppSettings();

    public AppSettings Current { get; private set; }
    public string SettingsFilePath => "(memory)";
    public bool FailSaves { get; set; }
    public int SaveCount { get; private set; }

    public event Action<AppSettings>? Changed;

    public AppSettings Load() => Current;

    public bool Save(AppSettings settings)
    {
        SaveCount++;
        if (FailSaves)
            return false;
        Current = settings.Clone();
        Changed?.Invoke(Current);
        return true;
    }
}

/// <summary>Records every call the router makes, and can be told to fail on demand.</summary>
public sealed class FakeTelegram : ITelegramClient
{
    public sealed record Sent(long ChatId, string Text, bool HasKeyboard, IReadOnlyList<string> Buttons,
                              IReadOnlyList<string> Payloads);
    public sealed record Edited(long ChatId, long MessageId, string Text, IReadOnlyList<string> Buttons,
                                IReadOnlyList<string> Payloads);
    public sealed record Answered(string CallbackId, string? Text, bool Alert);
    public sealed record File(long ChatId, string Name, int Bytes, bool IsPhoto);

    public List<Sent> Messages { get; } = new();
    public List<Edited> Edits { get; } = new();
    public List<Answered> Answers { get; } = new();
    public List<File> Files { get; } = new();
    public List<string> Actions { get; } = new();

    /// <summary>When set, every EditMessageAsync throws it — a panel Telegram will not edit.</summary>
    public Exception? EditFailure { get; set; }

    public bool IsConfigured => true;

    public void Configure(string workerUrl, string botToken, string proxySecret) { }

    public Task<TgUser> GetMeAsync(CancellationToken ct = default) =>
        Task.FromResult(new TgUser { Id = 1, IsBot = true, Username = "test_bot" });

    /// <summary>Custom emoji this fake will admit to knowing about, keyed by identifier.</summary>
    public Dictionary<string, TgSticker> CustomEmoji { get; } = new(StringComparer.Ordinal);

    /// <summary>Sticker sets this fake will hand back, keyed by name.</summary>
    public Dictionary<string, TgStickerSet> StickerSets { get; } = new(StringComparer.Ordinal);

    public List<string[]> CustomEmojiLookups { get; } = new();

    public Task<IReadOnlyList<TgSticker>> GetCustomEmojiStickersAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        CustomEmojiLookups.Add(ids.ToArray());
        IReadOnlyList<TgSticker> found = ids
            .Where(CustomEmoji.ContainsKey)
            .Select(id => CustomEmoji[id])
            .ToArray();
        return Task.FromResult(found);
    }

    public Task<TgStickerSet> GetStickerSetAsync(string name, CancellationToken ct = default) =>
        StickerSets.TryGetValue(name, out var set)
            ? Task.FromResult(set)
            : throw new InvalidOperationException($"No sticker set named '{name}'.");

    public Task DeleteWebhookAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default) =>
        Task.FromResult(new List<TgUpdate>());

    public Task<long?> SendMessageAsync(long chatId, string text, TgInlineKeyboardMarkup? keyboard = null, CancellationToken ct = default)
    {
        Messages.Add(new Sent(chatId, text, keyboard is not null, Captions(keyboard), Payloads(keyboard)));
        return Task.FromResult<long?>(Messages.Count);
    }

    public Task<long?> SendWithMarkupAsync(long chatId, string text, object? replyMarkup, CancellationToken ct = default)
    {
        Messages.Add(new Sent(chatId, text, replyMarkup is not null,
            Captions(replyMarkup as TgInlineKeyboardMarkup), Payloads(replyMarkup as TgInlineKeyboardMarkup)));
        return Task.FromResult<long?>(Messages.Count);
    }

    public Task<bool> EditMessageAsync(long chatId, long messageId, string text, TgInlineKeyboardMarkup? keyboard, CancellationToken ct = default)
    {
        if (EditFailure is not null)
            throw EditFailure;
        Edits.Add(new Edited(chatId, messageId, text, Captions(keyboard), Payloads(keyboard)));
        return Task.FromResult(true);
    }

    public Task SendPhotoAsync(long chatId, byte[] photo, string fileName, string? caption = null, CancellationToken ct = default)
    {
        Files.Add(new File(chatId, fileName, photo.Length, true));
        return Task.CompletedTask;
    }

    public Task SendDocumentAsync(long chatId, byte[] file, string fileName, string? caption = null, CancellationToken ct = default)
    {
        Files.Add(new File(chatId, fileName, file.Length, false));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackId, string? text = null, bool showAlert = false, CancellationToken ct = default)
    {
        Answers.Add(new Answered(callbackId, text, showAlert));
        return Task.CompletedTask;
    }

    public Task<TgFile> GetFileAsync(string fileId, CancellationToken ct = default) =>
        Task.FromResult(new TgFile { FileId = fileId, FilePath = "docs/" + fileId });

    public Task<byte[]> DownloadFileAsync(string filePath, long maxBytes, CancellationToken ct = default) =>
        Task.FromResult(new byte[] { 1, 2, 3 });

    public Task SendChatActionAsync(long chatId, string action, CancellationToken ct = default)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }

    public Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct = default)
        => Task.CompletedTask;

    private static IReadOnlyList<string> Captions(TgInlineKeyboardMarkup? keyboard) =>
        keyboard is null
            ? Array.Empty<string>()
            : keyboard.InlineKeyboard.SelectMany(row => row).Select(b => b.Text).ToArray();

    /// <summary>
    /// The callback payloads, which is what a test that cares about behaviour should
    /// assert on: captions are translated, payloads are protocol and never change.
    /// </summary>
    private static IReadOnlyList<string> Payloads(TgInlineKeyboardMarkup? keyboard) =>
        keyboard is null
            ? Array.Empty<string>()
            : keyboard.InlineKeyboard.SelectMany(row => row)
                .Select(b => b.CallbackData ?? string.Empty).ToArray();
}

/// <summary>Every control action, recorded rather than performed.</summary>
public sealed class FakeSystem : ISystemControlService
{
    public List<string> Calls { get; } = new();
    public string? TypedText { get; private set; }
    public string ClipboardText { get; set; } = "hello";
    public int? Volume { get; set; } = 40;
    public bool? Muted { get; set; } = false;

    private string Record(string name)
    {
        Calls.Add(name);
        return name + " done";
    }

    public Task<string> ShutdownAsync(int delaySeconds = 5) => Task.FromResult(Record("shutdown"));
    public Task<string> RestartAsync(int delaySeconds = 5) => Task.FromResult(Record("restart"));
    public Task<string> LogoffAsync() => Task.FromResult(Record("logoff"));
    public Task<string> CancelPendingAsync() => Task.FromResult(Record("abort"));
    public string Lock() => Record("lock");
    public string Sleep() => Record("sleep");
    public string Hibernate() => Record("hibernate");
    public string MonitorOff() => Record("monitoroff");
    public string SetVolume(int percent) { Volume = percent; return Record("setvolume"); }
    public string VolumeUp() => Record("volumeup");
    public string VolumeDown() => Record("volumedown");
    public string ToggleMute() { Muted = !(Muted ?? false); return Record("mute"); }
    public int? GetVolumePercent() => Volume;
    public bool? IsMuted() => Muted;
    public string MediaPlayPause() => Record("play");
    public string MediaNext() => Record("next");
    public string MediaPrevious() => Record("prev");
    public string KillProcess(string nameOrPid) => Record("kill:" + nameOrPid);
    public Task<string> RunShellCommandAsync(string command, CancellationToken ct = default)
        => Task.FromResult(Record("shell:" + command));
    public string GetClipboardText() { Calls.Add("getclip"); return ClipboardText; }
    public string SetClipboardText(string text) { ClipboardText = text; return Record("setclip"); }
    public string OpenTarget(string target) => Record("open:" + target);
    public string TypeText(string text) { TypedText = text; return Record("type"); }
    public Task<string> SpeakAsync(string text, CancellationToken ct = default) => Task.FromResult(Record("speak"));
}

public sealed class FakeScreenshots : IScreenshotService
{
    public int ScreenCount { get; set; } = 1;
    public bool AsPhoto { get; set; } = true;
    public List<int> Captured { get; } = new();

    public ScreenCapture CaptureAll(long maxPhotoBytes, int maxDimensionSum)
    {
        Captured.Add(-1);
        return new ScreenCapture(new byte[] { 1, 2, 3, 4 }, "desktop.png", AsPhoto);
    }

    public ScreenCapture CaptureScreen(int index, long maxPhotoBytes, int maxDimensionSum)
    {
        if (index < 0 || index >= ScreenCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        Captured.Add(index);
        return new ScreenCapture(new byte[] { 1, 2 }, $"monitor{index}.png", AsPhoto);
    }
}

public sealed class FakeInfo : ISystemInfoService
{
    /// <summary>When set, every report throws it — an unreadable drive, a vanished process.</summary>
    public Exception? Failure { get; set; }

    private string Report(string name) => Failure is not null ? throw Failure : $"<b>{name}</b>";

    public Task<string> GetSystemInfoAsync(CancellationToken ct = default) => Task.FromResult(Report("sysinfo"));
    public string GetDisks() => Report("disks");
    public string GetBattery() => Report("battery");
    public string GetTopProcesses(int count = 12) => Report("processes");
    public Task<string> GetNetworkAsync(CancellationToken ct = default) => Task.FromResult(Report("network"));
}

/// <summary>Records what the sign-in entry was told, so the pair of writes can be checked.</summary>
public sealed class FakeStartup : IStartupManager
{
    public bool Enabled { get; private set; }
    public List<bool> Calls { get; } = new();

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled)
    {
        Calls.Add(enabled);
        Enabled = enabled;
    }
}

/// <summary>
/// A machine whose Windows settings can be inspected and driven from a test. Shaped
/// like FakeSystem: every call is recorded, and Failure makes the whole surface throw
/// so the "this subsystem is unreadable" paths can be exercised.
/// </summary>
public sealed class FakePcSettings : IPcSettingsService
{
    public const string BalancedId = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string SaverId = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public List<string> Calls { get; } = new();
    public Exception? Failure { get; set; }

    public List<PowerPlan> Plans { get; set; } = new()
    {
        new PowerPlan(BalancedId, "Balanced", true),
        new PowerPlan(SaverId, "Power saver", false),
    };

    public BrightnessState Brightness { get; set; } = new(true, 50);
    public WifiState Wifi { get; set; } = new(true, true, "HomeNet");
    public List<string> Profiles { get; set; } = new() { "HomeNet", "Café Wi-Fi ☕" };
    public RadioPower Bluetooth { get; set; } = RadioPower.On;

    private T Record<T>(string name, T value)
    {
        if (Failure is not null)
            throw Failure;
        Calls.Add(name);
        return value;
    }

    public Task<IReadOnlyList<PowerPlan>> GetPowerPlansAsync(CancellationToken ct = default)
        => Task.FromResult(Record("plans", (IReadOnlyList<PowerPlan>)Plans));

    public Task<string> SetPowerPlanAsync(string planId, CancellationToken ct = default)
    {
        var result = Record("setplan:" + planId, "plan set");
        Plans = Plans.Select(p => p with { IsActive = p.Id == planId }).ToList();
        return Task.FromResult(result);
    }

    public BrightnessState GetBrightness() => Record("brightness", Brightness);

    public string SetBrightness(int percent)
    {
        var result = Record("setbrightness:" + percent, "brightness set");
        Brightness = new BrightnessState(true, percent);
        return result;
    }

    public Task<WifiState> GetWifiAsync(CancellationToken ct = default)
        => Task.FromResult(Record("wifi", Wifi));

    public Task<IReadOnlyList<string>> GetWifiProfilesAsync(CancellationToken ct = default)
        => Task.FromResult(Record("wifiprofiles", (IReadOnlyList<string>)Profiles));

    public Task<string> ConnectWifiAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult(Record("wificonnect:" + profileName, "connecting"));

    public Task<string> DisconnectWifiAsync(CancellationToken ct = default)
    {
        var result = Record("wifidisconnect", "disconnected");
        Wifi = new WifiState(true, false, null);
        return Task.FromResult(result);
    }

    public Task<RadioPower> GetBluetoothAsync(CancellationToken ct = default)
        => Task.FromResult(Record("bluetooth", Bluetooth));

    public Task<string> SetBluetoothAsync(bool on, CancellationToken ct = default)
    {
        var result = Record("setbluetooth:" + (on ? "1" : "0"), on ? "bt on" : "bt off");
        Bluetooth = on ? RadioPower.On : RadioPower.Off;
        return Task.FromResult(result);
    }
}
