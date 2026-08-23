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

    /// <summary>
    /// Ciphertext that could not be decrypted on load, kept verbatim so a save does
    /// not overwrite it. A DPAPI failure is usually about the environment — a profile
    /// that is not loaded yet, a file carried over from another machine — and the
    /// stored token is often still perfectly good for the account it belongs to.
    /// Blanking it on the next settings change would destroy it for good.
    /// </summary>
    private readonly Dictionary<string, string> _unreadable = new(StringComparer.Ordinal);

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
                _unreadable.Clear();
                loaded.CloudflareApiToken = Reveal(nameof(loaded.CloudflareApiToken), loaded.CloudflareApiToken);
                loaded.TelegramBotToken = Reveal(nameof(loaded.TelegramBotToken), loaded.TelegramBotToken);
                loaded.ProxySecret = Reveal(nameof(loaded.ProxySecret), loaded.ProxySecret);

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
                toStore.CloudflareApiToken = Conceal(nameof(settings.CloudflareApiToken), settings.CloudflareApiToken);
                toStore.TelegramBotToken = Conceal(nameof(settings.TelegramBotToken), settings.TelegramBotToken);
                toStore.ProxySecret = Conceal(nameof(settings.ProxySecret), settings.ProxySecret);

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

    /// <summary>Decrypts one field, remembering the ciphertext if it cannot be read.</summary>
    private string Reveal(string field, string? stored)
    {
        if (_protector.TryUnprotect(stored, out var plain))
            return plain;

        _unreadable[field] = stored ?? string.Empty;
        _log.Warning($"The stored {field} could not be decrypted on this account. " +
                     "It has been left untouched; enter it again on the Connect page to replace it.");
        return string.Empty;
    }

    /// <summary>
    /// Encrypts one field for storage. When the in-memory value is empty because the
    /// stored one could not be decrypted, the stored ciphertext goes back unchanged
    /// instead of an empty string. Encrypting a real value and failing aborts the whole
    /// save: a settings file missing one token is worse than one that is a save behind.
    /// </summary>
    private string Conceal(string field, string? plain)
    {
        if (string.IsNullOrEmpty(plain) && _unreadable.TryGetValue(field, out var original))
            return original;

        if (_protector.TryProtect(plain, out var cipher))
        {
            if (!string.IsNullOrEmpty(plain))
                _unreadable.Remove(field);
            return cipher;
        }

        throw new InvalidOperationException(
            $"Windows would not encrypt the {field} for storage, so the settings were not saved.");
    }
}
