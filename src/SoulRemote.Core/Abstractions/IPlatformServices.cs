namespace SoulRemote.Services;

/// <summary>
/// Everything the bot can do to the machine it runs on. The implementation is
/// Windows-only and lives in the desktop app; the core dispatches through this
/// interface so command routing can be tested without a real desktop.
/// </summary>
public interface ISystemControlService
{
    Task<string> ShutdownAsync(int delaySeconds = 5);
    Task<string> RestartAsync(int delaySeconds = 5);
    Task<string> LogoffAsync();
    Task<string> CancelPendingAsync();
    string Lock();
    string Sleep();
    string Hibernate();
    string MonitorOff();

    string SetVolume(int percent);
    string VolumeUp();
    string VolumeDown();
    string ToggleMute();

    /// <summary>Current output level as 0-100, or null when there is no audio endpoint.</summary>
    int? GetVolumePercent();

    /// <summary>Whether the default output is muted, or null when it cannot be read.</summary>
    bool? IsMuted();

    string MediaPlayPause();
    string MediaNext();
    string MediaPrevious();

    string KillProcess(string nameOrPid);
    Task<string> RunShellCommandAsync(string command, CancellationToken ct = default);

    string GetClipboardText();
    string SetClipboardText(string text);
    string OpenTarget(string target);
    string TypeText(string text);
    Task<string> SpeakAsync(string text, CancellationToken ct = default);
}

/// <summary>Read-only reports about the machine, rendered as Telegram-ready HTML.</summary>
public interface ISystemInfoService
{
    Task<string> GetSystemInfoAsync(CancellationToken ct = default);
    string GetDisks();
    string GetBattery();
    string GetTopProcesses(int count = 12);
    Task<string> GetNetworkAsync(CancellationToken ct = default);
}

/// <summary>
/// A finished capture, already shaped to something Telegram will accept.
/// <paramref name="IsPhoto"/> is false when the image had to be sent as a document
/// because it would not survive sendPhoto's limits even after downscaling.
/// </summary>
public sealed record ScreenCapture(byte[] Data, string FileName, bool IsPhoto);

public interface IScreenshotService
{
    /// <summary>
    /// Captures the full virtual desktop (all monitors), downscaling and re-encoding
    /// as needed so the result fits inside Telegram's photo limits.
    /// </summary>
    ScreenCapture CaptureAll(long maxPhotoBytes, int maxDimensionSum);

    /// <summary>Captures a single monitor (0-based), under the same limits.</summary>
    ScreenCapture CaptureScreen(int index, long maxPhotoBytes, int maxDimensionSum);

    int ScreenCount { get; }
}

public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
