using SoulRemote.Localization;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>
/// Preferences only. Connecting Cloudflare and Telegram lives on the Connect
/// page so this screen stays a list of decisions, not a setup flow.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private bool _loading;

    /// <param name="update">
    /// Shared with the shell rather than built here: the same object drives the card
    /// that appears over the app, and two of them would disagree about what stage the
    /// download had reached.
    /// </param>
    public SettingsViewModel(AppServices services, UpdateViewModel update)
    {
        _services = services;
        Update = update;
        Load();

        // The bot can change every one of these from Telegram now, so this page can no
        // longer assume it is the only writer. Settings.Changed already fires after
        // every successful save; without listening to it, a toggle flipped from a phone
        // would leave this window showing the old position until it was rebuilt.
        _services.Settings.Changed += OnSettingsChanged;
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenSettingsFileCommand = new RelayCommand(OpenSettingsFolder);
        SetLanguageCommand = new RelayCommand(p => SetLanguage(p as string));
    }

    /// <summary>
    /// Switching language rewrites this window, the tray menu and everything the bot
    /// says, so it is applied globally rather than kept as a view-model field. The
    /// "/" command list in Telegram is re-published too, since its descriptions are
    /// translated as well.
    /// </summary>
    private void SetLanguage(string? tag)
    {
        var language = AppLanguageExtensions.Parse(tag);
        if (language == Strings.Current)
            return;

        Persist(s => s.Language = language.Tag());
        Strings.Use(language);
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsPersian));
        _ = _services.Bot.RefreshCommandListAsync();
    }

    /// <summary>
    /// Re-reads everything after a save that did not come from this page. Raised on a
    /// background thread by the settings service, so it is marshalled before it
    /// touches anything bound to the window.
    /// </summary>
    private void OnSettingsChanged(Models.AppSettings settings) => UiThread.Post(() =>
    {
        Load();
        // Load() writes the backing fields directly, so every property is re-read at
        // once rather than each setter being told individually.
        OnPropertyChanged(string.Empty);
    });

    /// <summary>Re-reads the chips after the language changed somewhere else — the bot menu.</summary>
    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsPersian));
    }

    /// <summary>The updater, shown as a card on this page and as a modal from the shell.</summary>
    public UpdateViewModel Update { get; }

    public bool IsEnglish => Strings.Current == AppLanguage.English;
    public bool IsPersian => Strings.Current == AppLanguage.Persian;

    public RelayCommand SetLanguageCommand { get; }

    private void Load()
    {
        _loading = true;
        var s = _services.Settings.Current;
        _startWithWindows = _services.Startup.IsEnabled();
        _startMinimized = s.StartMinimized;
        _autoStartBot = s.AutoStartBot;
        _notifyOnStartup = s.NotifyOnStartup;
        _allowShellCommands = s.AllowShellCommands;
        _allowFileAccess = s.AllowFileAccess;
        _allowInputInjection = s.AllowInputInjection;
        _allowRemoteSettings = s.AllowRemoteSettings;
        _reduceMotion = s.ReduceMotion;
        _pollTimeoutSeconds = s.PollTimeoutSeconds <= 0 ? 25 : s.PollTimeoutSeconds;
        _logRetentionDays = s.LogRetentionDays;
        _downloadFolder = s.DownloadFolder;
        _loading = false;
    }

    // Each toggle saves immediately: there is no Save button to forget.
    private void Persist(Action<Models.AppSettings> mutate)
    {
        if (_loading)
            return;
        var s = _services.Settings.Current.Clone();
        mutate(s);
        // A write that did not land must not say it did: the toggle is already showing
        // the new position, and "Saved 14:02:11" underneath it would be the only thing
        // the user had to go on.
        SavedAt = _services.Settings.Save(s)
            ? Strings.Format("ui.settings.saved",
                DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
            : Strings.Get("ui.settings.savefailed");
    }

    private string _savedAt = string.Empty;
    public string SavedAt { get => _savedAt; private set => SetProperty(ref _savedAt, value); }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value) || _loading) return;
            _services.Startup.SetEnabled(value);
            Persist(s => s.StartWithWindows = value);
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set { if (SetProperty(ref _startMinimized, value)) Persist(s => s.StartMinimized = value); }
    }

    private bool _autoStartBot;
    public bool AutoStartBot
    {
        get => _autoStartBot;
        set { if (SetProperty(ref _autoStartBot, value)) Persist(s => s.AutoStartBot = value); }
    }

    private bool _notifyOnStartup;
    public bool NotifyOnStartup
    {
        get => _notifyOnStartup;
        set { if (SetProperty(ref _notifyOnStartup, value)) Persist(s => s.NotifyOnStartup = value); }
    }

    private bool _allowShellCommands;
    public bool AllowShellCommands
    {
        get => _allowShellCommands;
        set { if (SetProperty(ref _allowShellCommands, value)) Persist(s => s.AllowShellCommands = value); }
    }

    private bool _allowFileAccess;
    public bool AllowFileAccess
    {
        get => _allowFileAccess;
        set { if (SetProperty(ref _allowFileAccess, value)) Persist(s => s.AllowFileAccess = value); }
    }

    private bool _allowInputInjection;
    public bool AllowInputInjection
    {
        get => _allowInputInjection;
        set { if (SetProperty(ref _allowInputInjection, value)) Persist(s => s.AllowInputInjection = value); }
    }

    private bool _allowRemoteSettings;
    public bool AllowRemoteSettings
    {
        get => _allowRemoteSettings;
        set { if (SetProperty(ref _allowRemoteSettings, value)) Persist(s => s.AllowRemoteSettings = value); }
    }

    private bool _reduceMotion;
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { if (SetProperty(ref _reduceMotion, value)) Persist(s => s.ReduceMotion = value); }
    }

    private int _pollTimeoutSeconds;
    public int PollTimeoutSeconds
    {
        get => _pollTimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 50);
            if (SetProperty(ref _pollTimeoutSeconds, clamped))
                Persist(s => s.PollTimeoutSeconds = clamped);

            if (clamped != value)
                SnapBack(nameof(PollTimeoutSeconds));
        }
    }

    /// <summary>
    /// If we coerced what the user typed, the TextBox is still showing their number.
    /// Re-notify after the binding's own update pass so it snaps back to the value
    /// that was actually saved.
    /// </summary>
    private void SnapBack(string propertyName)
        => Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.DataBind,
            () => OnPropertyChanged(propertyName));

    private int _logRetentionDays;
    public int LogRetentionDays
    {
        get => _logRetentionDays;
        set
        {
            var clamped = Math.Clamp(value, 0, 365);
            if (SetProperty(ref _logRetentionDays, clamped))
                Persist(s => s.LogRetentionDays = clamped);
            if (clamped != value)
                SnapBack(nameof(LogRetentionDays));
        }
    }

    private string _downloadFolder = string.Empty;
    public string DownloadFolder
    {
        get => _downloadFolder;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (SetProperty(ref _downloadFolder, trimmed))
                Persist(s => s.DownloadFolder = trimmed);
        }
    }

    public string SettingsPath => _services.Settings.SettingsFilePath;

    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenSettingsFileCommand { get; }

    private void OpenLogFolder()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulRemote", "logs");
        OpenPath(dir);
    }

    private void OpenSettingsFolder()
    {
        var dir = System.IO.Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
            OpenPath(dir);
    }

    private void OpenPath(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"Could not open {path}: {ex.Message}");
        }
    }
}
