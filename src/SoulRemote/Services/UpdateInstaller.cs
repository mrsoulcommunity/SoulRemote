using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoulRemote.Services;

/// <summary>
/// Starts a downloaded setup package and steps out of its way.
///
/// The installer replaces SoulRemote.exe, which this process is running from, so the
/// two cannot overlap: the caller starts the installer here and then closes the app
/// immediately. LAUNCHAFTERINSTALL is what brings it back — the package starts the
/// newly installed exe once it is done, which is the whole point on a machine whose
/// owner is not sitting in front of it.
/// </summary>
public sealed class UpdateInstaller : IUpdateInstaller
{
    /// <summary>Where the MSI records the folder it installed into.</summary>
    private const string InstallKey = @"Software\MrSoul\SoulRemote";

    /// <summary>Set on the package's command line so it restarts the app when it finishes.</summary>
    private const string LaunchProperty = "LAUNCHAFTERINSTALL=1";

    private readonly ILogService _log;

    public UpdateInstaller(ILogService log)
    {
        _log = log;
        InstallFolder = ReadInstallFolder(log);
    }

    public string? InstallFolder { get; }

    public bool CanReplaceItself
    {
        get
        {
            if (string.IsNullOrEmpty(InstallFolder))
                return false;
            var running = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            return !string.IsNullOrEmpty(running) && SamePath(running, InstallFolder);
        }
    }

    public bool Start(string installerPath, bool silent)
    {
        try
        {
            if (!File.Exists(installerPath))
            {
                _log.Error($"The update installer is no longer at {installerPath}.");
                return false;
            }

            var psi = Build(installerPath, silent);
            _log.Info($"Starting the update installer: {psi.FileName} {psi.Arguments}");

            using var process = Process.Start(psi);
            if (process is null)
            {
                _log.Error("Windows did not start the update installer.");
                return false;
            }

            // Exiting on our own kills nothing: a started process outlives its parent.
            // What it must not do is inherit our console-less window station oddities,
            // hence a plain start from the folder the package sits in.
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not start the update installer", ex);
            return false;
        }
    }

    /// <summary>
    /// Builds the command line. A bundled setup.exe takes Burn's switches, a bare .msi
    /// has to go through msiexec, and both are asked to write a verbose log next to the
    /// app's own — an update that fails on someone else's machine is otherwise invisible.
    /// </summary>
    private ProcessStartInfo Build(string installerPath, bool silent)
    {
        var logPath = Path.Combine(_log.LogDirectory,
            "update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".log");

        var isMsi = Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = isMsi ? "msiexec.exe" : installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isMsi)
        {
            psi.Arguments = string.Join(' ',
                "/i", Quote(installerPath),
                silent ? "/qn" : "/passive",
                "/norestart",
                "/l*v", Quote(logPath),
                LaunchProperty);
        }
        else
        {
            psi.Arguments = string.Join(' ',
                silent ? "/quiet" : "/passive",
                "/norestart",
                "/log", Quote(logPath),
                LaunchProperty);
        }

        return psi;
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private static string? ReadInstallFolder(ILogService log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKey, false);
            return key?.GetValue("InstallFolder") as string;
        }
        catch (Exception ex)
        {
            log.Debug($"Could not read the install folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Compares two folders as Windows would. The registry value comes from the MSI and
    /// carries a trailing separator; the running exe's folder does not.
    /// </summary>
    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
