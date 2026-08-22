using System.Diagnostics;
using System.Text;
using SoulRemote.Services.Native;

namespace SoulRemote.Services;

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

    string MediaPlayPause();
    string MediaNext();
    string MediaPrevious();

    string KillProcess(string nameOrPid);
    Task<string> RunShellCommandAsync(string command, CancellationToken ct = default);
}

public sealed class SystemControlService : ISystemControlService
{
    private readonly ILogService _log;

    public SystemControlService(ILogService log) => _log = log;

    public Task<string> ShutdownAsync(int delaySeconds = 5)
        => RunProcessAsync("shutdown.exe", $"/s /f /t {Math.Max(0, delaySeconds)}",
            $"System will shut down in {delaySeconds} seconds. Use /cancel to abort.");

    public Task<string> RestartAsync(int delaySeconds = 5)
        => RunProcessAsync("shutdown.exe", $"/r /f /t {Math.Max(0, delaySeconds)}",
            $"System will restart in {delaySeconds} seconds. Use /cancel to abort.");

    public Task<string> LogoffAsync()
        => RunProcessAsync("shutdown.exe", "/l", "Logging off the current user...");

    public Task<string> CancelPendingAsync()
        => RunProcessAsync("shutdown.exe", "/a", "Pending shutdown/restart cancelled.");

    public string Lock()
    {
        if (NativeMethods.LockWorkStation())
            return "Workstation locked.";
        throw new InvalidOperationException("LockWorkStation failed.");
    }

    public string Sleep()
    {
        if (NativeMethods.SetSuspendState(false, false, false))
            return "System going to sleep.";
        throw new InvalidOperationException("SetSuspendState (sleep) failed.");
    }

    public string Hibernate()
    {
        if (NativeMethods.SetSuspendState(true, false, false))
            return "System hibernating.";
        throw new InvalidOperationException("Hibernate failed (it may be disabled on this machine).");
    }

    public string MonitorOff()
    {
        NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(NativeMethods.MONITOR_OFF));
        return "Display turned off.";
    }

    public string SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (CoreAudio.SetVolume(percent / 100f))
            return $"Volume set to {percent}%.";
        throw new InvalidOperationException("Unable to set volume.");
    }

    public string VolumeUp()
    {
        NativeMethods.TapKey(NativeMethods.VK_VOLUME_UP);
        return $"Volume up. {CurrentVolumeText()}";
    }

    public string VolumeDown()
    {
        NativeMethods.TapKey(NativeMethods.VK_VOLUME_DOWN);
        return $"Volume down. {CurrentVolumeText()}";
    }

    public string ToggleMute()
    {
        var current = CoreAudio.IsMuted();
        if (current is null)
        {
            NativeMethods.TapKey(NativeMethods.VK_VOLUME_MUTE);
            return "Toggled mute.";
        }
        CoreAudio.SetMute(!current.Value);
        return current.Value ? "Unmuted." : "Muted.";
    }

    public string MediaPlayPause()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
        return "Media play/pause.";
    }

    public string MediaNext()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
        return "Next track.";
    }

    public string MediaPrevious()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_PREV_TRACK);
        return "Previous track.";
    }

    public string KillProcess(string nameOrPid)
    {
        nameOrPid = nameOrPid.Trim();
        if (string.IsNullOrEmpty(nameOrPid))
            throw new ArgumentException("Provide a process name or PID.");

        if (int.TryParse(nameOrPid, out var pid))
        {
            var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            proc.Kill(true);
            return $"Killed process {name} (PID {pid}).";
        }

        var trimmed = nameOrPid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? nameOrPid[..^4] : nameOrPid;
        var matches = Process.GetProcessesByName(trimmed);
        if (matches.Length == 0)
            return $"No running process named '{nameOrPid}'.";
        var killed = 0;
        foreach (var p in matches)
        {
            try { p.Kill(true); killed++; }
            catch (Exception ex) { _log.Warning($"Could not kill PID {p.Id}: {ex.Message}"); }
        }
        return $"Killed {killed} instance(s) of '{trimmed}'.";
    }

    public async Task<string> RunShellCommandAsync(string command, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            return "Command timed out after 60 seconds and was terminated.";
        }

        var output = (await stdOutTask.ConfigureAwait(false)) + (await stdErrTask.ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(output))
            output = $"(no output, exit code {proc.ExitCode})";
        return output;
    }

    private static string CurrentVolumeText()
    {
        var v = CoreAudio.GetVolume();
        return v is null ? string.Empty : $"Now ~{Math.Round(v.Value * 100)}%.";
    }

    private async Task<string> RunProcessAsync(string file, string args, string successMessage)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            _log.Warning($"{file} {args} exited {proc.ExitCode}: {err}");
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(err) ? $"{file} exited with code {proc.ExitCode}." : err.Trim());
        }
        return successMessage;
    }
}
