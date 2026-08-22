using System.IO;
using System.Text.Json;
using SoulRemote.Models;
using SoulRemote.Services.Security;

namespace SoulRemote.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    AppSettings Load();
    void Save(AppSettings settings);
    string SettingsFilePath { get; }
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under %APPDATA%\SoulRemote.
/// Secret fields are DPAPI-encrypted before hitting disk and decrypted on load.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly ILogService _log;
    private readonly object _ioLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string SettingsFilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public SettingsService(ILogService log)
    {
        _log = log;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulRemote");
        Directory.CreateDirectory(dir);
        SettingsFilePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        lock (_ioLock)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Current = new AppSettings();
                    return Current;
                }

                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

                // Decrypt secrets that were stored protected.
                loaded.CloudflareApiToken = DataProtector.Unprotect(loaded.CloudflareApiToken);
                loaded.TelegramBotToken = DataProtector.Unprotect(loaded.TelegramBotToken);
                loaded.ProxySecret = DataProtector.Unprotect(loaded.ProxySecret);

                Current = loaded;
                _log.Info("Settings loaded.");
                return Current;
            }
            catch (Exception ex)
            {
                _log.Error("Failed to load settings, using defaults", ex);
                Current = new AppSettings();
                return Current;
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_ioLock)
        {
            try
            {
                // Serialize a copy whose secrets are encrypted.
                var toStore = settings.Clone();
                toStore.CloudflareApiToken = DataProtector.Protect(settings.CloudflareApiToken);
                toStore.TelegramBotToken = DataProtector.Protect(settings.TelegramBotToken);
                toStore.ProxySecret = DataProtector.Protect(settings.ProxySecret);

                var json = JsonSerializer.Serialize(toStore, JsonOptions);

                // Write atomically via a temp file + replace.
                var tmp = SettingsFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(SettingsFilePath))
                    File.Replace(tmp, SettingsFilePath, null);
                else
                    File.Move(tmp, SettingsFilePath);

                // Keep the live copy holding decrypted secrets for in-process use.
                Current = settings.Clone();
                _log.Info("Settings saved.");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to save settings", ex);
            }
        }
    }
}
