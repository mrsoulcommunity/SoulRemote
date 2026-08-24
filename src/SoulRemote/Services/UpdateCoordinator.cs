using System.Threading;
using System.Threading.Tasks;
using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

    /// <summary>
/// Decides when Soul Remote looks for a new release and what it does with one.
///
/// The pieces either side of it are deliberately dumb: <see cref="IAppUpdateService"/>
/// only knows how to ask GitHub and verify what comes back, and
/// <see cref="IUpdateInstaller"/> only knows how to start a package. The policy — check
/// at startup and once a day, apply silently only when the user left that switched on
/// and only to a copy an installer owns, remember the window and relay state across the
/// restart — is all here, in one readable place.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    /// <summary>
    /// How long the first check waits after launch. Long enough for the window to be up
    /// and for the relay to have had the network to itself, short enough that "there is
    /// a new version" is still part of opening the app rather than a surprise later on.
    /// </summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(6);

    /// <summary>Daily. A tray app runs for weeks, so this is the cadence that matters.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IAppUpdateService _updates;
    private readonly IUpdateInstaller _installer;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    private readonly CancellationTokenSource _stopping = new();
    private System.Threading.Timer? _timer;
    private int _running;

    /// <summary>
    /// Versions that have already been tried and failed on this machine. Without it a
    /// release the installer refuses would be downloaded and attempted again every day,
    /// for as long as it stays the latest one.
    /// </summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the window is hidden in the tray right now. Set by the app.</summary>
    public Func<bool>? IsHiddenInTray { get; set; }

    /// <summary>Whether the relay is currently up. Set by the app.</summary>
    public Func<bool>? IsRelayRunning { get; set; }

    /// <summary>Closes the app so the installer can replace it. Set by the app.</summary>
    public Action? RequestShutdown { get; set; }

    /// <summary>Raised whenever the stage, message or progress changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised once per check that found a newer release, so the shell can put the card
    /// in front of the user. Never raised when there is nothing new — a check that comes
    /// back clean should be invisible.
    /// </summary>
    public event Action<AppRelease>? UpdateFound;

    /// <summary>
    /// True from the moment the installer is handed over until this process ends. The
    /// card reads it so it stops offering a button that would start a second installer.
    /// </summary>
    public bool IsInstalling { get; private set; }

    public UpdateCoordinator(
        IAppUpdateService updates, IUpdateInstaller installer, ISettingsService settings, ILogService log)
    {
        _updates = updates;
        _installer = installer;
        _settings = settings;
        _log = log;
        _updates.Changed += OnUpdatesChanged;
    }

    public IAppUpdateService Updates => _updates;
    public IUpdateInstaller Installer => _installer;

    /// <summary>
    /// Begins the background schedule. The timer is armed even when checking is
    /// switched off, and the tick reads the setting instead - otherwise turning the
    /// switch back on would do nothing at all until the next restart, which is not what
    /// a switch that says "check for updates" appears to promise.
    /// </summary>
    public void Start()
    {
        if (!_settings.Current.AutoCheckUpdates)
            _log.Debug("Automatic update checks are switched off.");

        _timer = new System.Threading.Timer(_ => _ = RunScheduledAsync(), null, FirstCheckDelay, CheckInterval);
    }

    private async Task RunScheduledAsync()
    {
        // The setting can be turned off between ticks; the timer keeps running so it can
        // be turned back on without restarting the app.
        if (!_settings.Current.AutoCheckUpdates || _stopping.IsCancellationRequested)
            return;

        try
        {
            await CheckAsync(userInitiated: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"Scheduled update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Looks for a release and, when the user has left automatic installs on, applies it.
    /// A user-initiated check never installs on its own — the button next to it does that,
    /// so the click that asked "is there one?" is not the click that replaces the app.
    /// </summary>
    public async Task<AppRelease?> CheckAsync(bool userInitiated)
    {
        // One at a time, and never two overlapping installs.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return _updates.Latest;

        try
        {
            var release = await _updates.CheckAsync(_stopping.Token).ConfigureAwait(false);
            if (release is null)
                return null;

            UpdateFound?.Invoke(release);
            if (userInitiated)
                return release;

            if (!_settings.Current.AutoInstallUpdates)
            {
                _log.Info($"Update {release.Version.ToString(3)} is available; automatic installs are off.");
                return release;
            }

            if (_refused.Contains(release.Tag))
            {
                _log.Debug($"Update {release.Tag} already failed on this machine; not retrying it.");
                return release;
            }

            if (!_installer.CanReplaceItself)
            {
                _log.Warning("An update is available, but this copy was not put here by the installer, " +
                             "so it cannot replace itself. Install the new version from the release page.");
                return release;
            }

            await ApplyCoreAsync(release, silent: true).ConfigureAwait(false);
            return release;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Downloads and applies the release found by the last check, showing progress.</summary>
    public async Task ApplyAsync()
    {
        var release = _updates.Latest;
        if (release is null)
            return;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            await ApplyCoreAsync(release, silent: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The app is closing; the part-finished download is cleaned up by the service.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task ApplyCoreAsync(AppRelease release, bool silent)
    {
        if (AlreadyInstalled(release))
            return;

        var path = _updates.InstallerPath
                   ?? await _updates.FetchAsync(release, _stopping.Token).ConfigureAwait(false);
        if (path is null)
        {
            _refused.Add(release.Tag);
            return;
        }

        // Written before the installer starts, because after it starts this process has
        // no guarantee of getting another instruction in.
        _updates.MarkPending(new PendingUpdate
        {
            Version = release.Version.ToString(3),
            Minimized = IsHiddenInTray?.Invoke() ?? false,
            RelayWasRunning = IsRelayRunning?.Invoke() ?? false,
            At = DateTimeOffset.Now,
        });

        IsInstalling = true;
        Changed?.Invoke();

        if (!_installer.Start(path, silent))
        {
            IsInstalling = false;
            // Nothing is going to read the record now, and leaving it would make the
            // next ordinary start think it had just been updated.
            _updates.TakePending();
            _refused.Add(release.Tag);
            Changed?.Invoke();
            return;
        }

        _log.Info($"Applying update {release.Version.ToString(3)}; Soul Remote is closing so it can be replaced.");
        RequestShutdown?.Invoke();
    }

    /// <summary>
    /// Whether this machine already has the release the check just offered.
    ///
    /// It is a contradiction — the running build said it was older — and it means one
    /// of the two numbers is wrong: the installer registered x.y.z while the assembly
    /// inside it still calls itself something earlier. Handing over anyway is the worst
    /// of the available answers, because the installer plans nothing, exits reporting
    /// success, and never runs the action that starts the app again — so the app closes
    /// itself, stays closed, and does it again the next time somebody opens it. Refusing
    /// costs the update and keeps the app.
    /// </summary>
    private bool AlreadyInstalled(AppRelease release)
    {
        var installed = _installer.InstalledVersion;
        if (installed is null || installed < release.Version)
            return false;

        _refused.Add(release.Tag);
        _updates.ReportFailure(Strings.Format("ui.update.stale", release.Version.ToString(3)));
        _log.Error(
            $"Refusing to apply {release.Version.ToString(3)}: the installer has already registered " +
            $"{installed.ToString(3)} on this machine, but this build reports itself as " +
            $"{_updates.CurrentVersion.ToString(3)}. Running the package again would replace nothing " +
            "and close the app for good. Reinstall from the release page.");
        Changed?.Invoke();
        return true;
    }

    private void OnUpdatesChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _updates.Changed -= OnUpdatesChanged;
        try { _stopping.Cancel(); } catch { /* already disposed */ }
        _timer?.Dispose();
        _stopping.Dispose();
    }
}
