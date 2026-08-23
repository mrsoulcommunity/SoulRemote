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

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Load();
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

    /// <summary>Re-reads the chips after the language changed somewhere else — the bot menu.</summary>
    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsPersian));
    }

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
        _services.Settings.Save(s);
        SavedAt = Strings.Format("ui.settings.saved", DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
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
