using System.Globalization;
using System.Threading.Tasks;
using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>
/// The face of the updater: one card that the shell shows over everything when a new
/// version turns up, and the same numbers again on the Settings page.
///
/// It owns no policy. Whether to check, whether to install unattended and what to do
/// with the window afterwards are the coordinator's decisions; this only renders what
/// stage the machinery is at and offers the two buttons a person can press.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly UpdateCoordinator _coordinator;

    /// <summary>Release notes are shown, not read aloud; anything past this is noise on a card.</summary>
    private const int MaxNotesLength = 2000;

    public UpdateViewModel(AppServices services)
    {
        _services = services;
        _coordinator = services.UpdateCoordinator;

        _autoCheck = services.Settings.Current.AutoCheckUpdates;
        _autoInstall = services.Settings.Current.AutoInstallUpdates;

        CheckCommand = new AsyncRelayCommand(() => _coordinator.CheckAsync(userInitiated: true));
        UpdateNowCommand = new AsyncRelayCommand(_coordinator.ApplyAsync, () => CanUpdateNow);
        LaterCommand = new RelayCommand(() => IsCardOpen = false);
        ReopenCommand = new RelayCommand(() => IsCardOpen = true);
        OpenReleaseCommand = new RelayCommand(OpenReleasePage);

        _coordinator.Changed += OnCoordinatorChanged;
        Strings.LanguageChanged += Refresh;
    }

    // ---- what the shell binds to ------------------------------------------

    private bool _isCardOpen;

    /// <summary>Whether the modal card is over the app right now.</summary>
    public bool IsCardOpen
    {
        get => _isCardOpen;
        set => SetProperty(ref _isCardOpen, value);
    }

    /// <summary>There is a newer release. Drives the badge in the rail.</summary>
    public bool HasUpdate => _coordinator.Updates.Latest is not null;

    /// <summary>The stage the machinery is at, straight from the service.</summary>
    public UpdateStage Stage => _coordinator.Updates.Stage;

    public string CurrentVersion => _coordinator.Updates.CurrentVersion.ToString(3);

    /// <summary>"Soul Remote 1.0.2" — the headline on the card.</summary>
    public string Headline => Strings.Format("ui.update.headline",
        _coordinator.Updates.Latest?.Version.ToString(3) ?? CurrentVersion);

    /// <summary>Version, size and publication date, in that order and machine-readable.</summary>
    public string Meta
    {
        get
        {
            var release = _coordinator.Updates.Latest;
            if (release is null)
                return string.Empty;

            var megabytes = release.Installer.Size / 1024d / 1024d;
            var parts = new List<string>
            {
                release.Tag,
                megabytes >= 0.1
                    ? megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                    : release.Installer.Name,
            };
            if (release.PublishedAt is { } published)
                parts.Add(published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The release body as published, trimmed to something a card can hold.</summary>
    public string Notes
    {
        get
        {
            var notes = _coordinator.Updates.Latest?.Notes ?? string.Empty;
            notes = notes.Replace("\r\n", "\n").Trim();
            return notes.Length <= MaxNotesLength ? notes : notes[..MaxNotesLength].TrimEnd() + " …";
        }
    }

    public bool HasNotes => Notes.Length > 0;

    /// <summary>One line describing exactly where the update has got to.</summary>
    public string Status
    {
        get
        {
            if (_coordinator.IsInstalling)
                return Strings.Format("ui.update.installing", LatestOrCurrent);

            return Stage switch
            {
                UpdateStage.Checking => Strings.Get("ui.update.checking"),
                UpdateStage.UpToDate => Strings.Format("ui.update.uptodate", CurrentVersion),
                UpdateStage.Available => Strings.Format("ui.update.available", LatestOrCurrent),
                UpdateStage.Downloading => Strings.Format("ui.update.downloading", LatestOrCurrent, Percent),
                UpdateStage.Ready => Strings.Format("ui.update.ready", LatestOrCurrent),
                UpdateStage.Installing => Strings.Format("ui.update.installing", LatestOrCurrent),
                UpdateStage.Failed => Strings.Format("ui.update.failed", _coordinator.Updates.Message),
                _ => Strings.Format("ui.update.idle", CurrentVersion),
            };
        }
    }

    private string LatestOrCurrent => _coordinator.Updates.Latest?.Version.ToString(3) ?? CurrentVersion;

    public int Percent => _coordinator.Updates.DownloadPercent;

    /// <summary>
    /// Whether the status line says anything the card is not already saying. At the
    /// moment an update is found the headline is "Soul Remote 1.2.0 is out" and the
    /// status line would be "Version 1.2.0 is out" directly underneath it, which is
    /// noise standing where progress is about to appear.
    /// </summary>
    public bool ShowStatus => Stage != UpdateStage.Available || _coordinator.IsInstalling;

    /// <summary>A bar is only honest while something is actually moving.</summary>
    public bool ShowProgress => Stage is UpdateStage.Checking or UpdateStage.Downloading || _coordinator.IsInstalling;

    /// <summary>Checking and installing have no percentage to report; downloading does.</summary>
    public bool ProgressIsIndeterminate => Stage is not UpdateStage.Downloading;

    /// <summary>The update failed and the message is worth showing in fault colour.</summary>
    public bool HasFault => Stage == UpdateStage.Failed;

    /// <summary>
    /// A copy that the installer did not put here cannot replace itself, so the card
    /// offers the release page instead of a button that would quietly do the wrong thing.
    /// </summary>
    public bool IsPortableCopy => !_coordinator.Installer.CanReplaceItself;

    public bool CanUpdateNow =>
        !IsPortableCopy && !_coordinator.IsInstalling &&
        Stage is UpdateStage.Available or UpdateStage.Ready;

    public bool HasReleasePage => !string.IsNullOrWhiteSpace(_coordinator.Updates.Latest?.ReleaseUrl);

    // ---- the two switches, mirrored on the Settings page -------------------

    private bool _autoCheck;
    public bool AutoCheck
    {
        get => _autoCheck;
        set
        {
            if (!SetProperty(ref _autoCheck, value))
                return;
            Persist(s => s.AutoCheckUpdates = value);
            // Turning checking off also turns unattended installs off: leaving the
            // second switch on while nothing checks would say something untrue.
            if (!value && AutoInstall)
                AutoInstall = false;
        }
    }

    private bool _autoInstall;
    public bool AutoInstall
    {
        get => _autoInstall;
        set
        {
            if (!SetProperty(ref _autoInstall, value))
                return;
            Persist(s => s.AutoInstallUpdates = value);
            if (value && !AutoCheck)
                AutoCheck = true;
        }
    }

    private void Persist(Action<AppSettings> mutate)
    {
        var settings = _services.Settings.Current.Clone();
        mutate(settings);
        _services.Settings.Save(settings);
    }

    // ---- commands ----------------------------------------------------------

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand UpdateNowCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand ReopenCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }

    private void OpenReleasePage()
    {
        var url = _coordinator.Updates.Latest?.ReleaseUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"Could not open the release page: {ex.Message}");
        }
    }

    // ---- change plumbing ---------------------------------------------------

    private void OnCoordinatorChanged() => UiThread.Post(Refresh);

    /// <summary>
    /// Everything on this view model is computed from the service, so one sweep after
    /// any change is both simpler and less error-prone than tracking which of a dozen
    /// derived values a given transition touched.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressIsIndeterminate));
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(IsPortableCopy));
        OnPropertyChanged(nameof(CanUpdateNow));
        OnPropertyChanged(nameof(HasReleasePage));

        // The buttons are ICommands, and WPF only re-asks CanExecute when something
        // pokes the command manager. Without this the primary button stays enabled
        // through the download it just started.
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Called by the shell when a check has just found something. Opening the card is
    /// the whole point of the feature — the user is told, once, and one button does the
    /// rest — but it must not appear over an unattended install that is already running.
    /// </summary>
    public void OfferUpdate()
    {
        Refresh();
        if (HasUpdate && !_coordinator.IsInstalling && !AutoInstall)
            IsCardOpen = true;
    }
}
