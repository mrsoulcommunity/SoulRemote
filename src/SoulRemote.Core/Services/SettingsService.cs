using System.IO;
using System.Text.Json;
using SoulRemote.Abstractions;
using SoulRemote.Models;

namespace SoulRemote.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    AppSettings Load();
    /// <summary>Persists the settings. Returns false when the write did not reach disk.</summary>
    bool Save(AppSettings settings);
    string SettingsFilePath { get; }

    /// <summary>Raised after a save, so anything caching a value can re-read it.</summary>
    event Action<AppSettings>? Changed;
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under %APPDATA%\SoulRemote.
/// Secret fields go through the supplied protector before hitting disk (DPAPI on
/// Windows) and come back out on load.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly ILogService _log;
    private readonly ISecretProtector _protector;
    private readonly object _ioLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string SettingsFilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    public SettingsService(ILogService log, ISecretProtector? protector = null, string? settingsPath = null)
    {
        _log = log;
        _protector = protector ?? NullSecretProtector.Instance;

        if (settingsPath is { Length: > 0 })
        {
            SettingsFilePath = settingsPath;
            var parent = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
        }
        else
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulRemote");
            Directory.CreateDirectory(dir);
            SettingsFilePath = Path.Combine(dir, "settings.json");
        }
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
                loaded.CloudflareApiToken = _protector.Unprotect(loaded.CloudflareApiToken);
                loaded.TelegramBotToken = _protector.Unprotect(loaded.TelegramBotToken);
                loaded.ProxySecret = _protector.Unprotect(loaded.ProxySecret);

                // A settings file edited by hand can hold anything; clamp rather than trust.
                loaded.Normalize();

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

    public bool Save(AppSettings settings)
    {
        AppSettings? saved = null;
        lock (_ioLock)
        {
            try
            {
                settings.Normalize();

                // Serialize a copy whose secrets are encrypted.
                var toStore = settings.Clone();
                toStore.CloudflareApiToken = _protector.Protect(settings.CloudflareApiToken);
                toStore.TelegramBotToken = _protector.Protect(settings.TelegramBotToken);
                toStore.ProxySecret = _protector.Protect(settings.ProxySecret);

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
                saved = Current;
                _log.Info("Settings saved.");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to save settings", ex);
            }
        }

        // Raised outside the lock: a handler that saves again would otherwise deadlock
        // on a non-reentrant path, and handlers can take as long as they like.
        if (saved is null)
            return false;
        Changed?.Invoke(saved);
        return true;
    }
}
