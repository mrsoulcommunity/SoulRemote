using System.Diagnostics;
using System.Globalization;
using System.Text;
using SoulRemote.Localization;
using SoulRemote.Services.Native;

namespace SoulRemote.Services;

public sealed class SystemControlService : ISystemControlService
{
    /// <summary>How long a /cmd command or a speech request may run before it is killed.</summary>
    private const int ShellTimeoutSeconds = 60;

    /// <summary>shutdown.exe and friends answer in milliseconds; this is only a backstop.</summary>
    private const int ProcessTimeoutSeconds = 20;

    private readonly ILogService _log;

    public SystemControlService(ILogService log) => _log = log;

    public Task<string> ShutdownAsync(int delaySeconds = 5)
        => RunProcessAsync("shutdown.exe", $"/s /f /t {Math.Max(0, delaySeconds)}",
            Strings.Format("act.shutdown", delaySeconds));

    public Task<string> RestartAsync(int delaySeconds = 5)
        => RunProcessAsync("shutdown.exe", $"/r /f /t {Math.Max(0, delaySeconds)}",
            Strings.Format("act.restart", delaySeconds));

    public Task<string> LogoffAsync()
        => RunProcessAsync("shutdown.exe", "/l", Strings.Get("act.logoff"));

    public Task<string> CancelPendingAsync()
        => RunProcessAsync("shutdown.exe", "/a", Strings.Get("act.aborted"));

    public string Lock()
    {
        if (NativeMethods.LockWorkStation())
            return Strings.Get("act.locked");
        throw new InvalidOperationException(Strings.Get("act.lockfailed"));
    }

    public string Sleep()
    {
        if (NativeMethods.SetSuspendState(false, false, false))
            return Strings.Get("act.sleeping");
        throw new InvalidOperationException(Strings.Get("act.sleepfailed"));
    }

    public string Hibernate()
    {
        if (NativeMethods.SetSuspendState(true, false, false))
            return Strings.Get("act.hibernating");
        throw new InvalidOperationException(Strings.Get("act.hibernatefailed"));
    }

    public string MonitorOff()
    {
        // SendMessage to HWND_BROADCAST blocks until every top-level window has
        // processed the message, and one hung window would hang the caller with it —
        // which, on the quick-controls path, is the UI thread. SendMessageTimeout
        // bounds that: an app that will not answer in two seconds is skipped.
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(NativeMethods.MONITOR_OFF),
            NativeMethods.SMTO_ABORTIFHUNG, 2000, out _);
        return Strings.Get("act.displayoff");
    }

    public string SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (CoreAudio.SetVolume(percent / 100f))
            return Strings.Format("act.volumeset", percent);
        throw new InvalidOperationException(Strings.Get("act.volumefailed"));
    }

    public string VolumeUp()
    {
        NativeMethods.TapKey(NativeMethods.VK_VOLUME_UP);
        return Strings.Format("act.volumeup", CurrentVolumeText());
    }

    public string VolumeDown()
    {
        NativeMethods.TapKey(NativeMethods.VK_VOLUME_DOWN);
        return Strings.Format("act.volumedown", CurrentVolumeText());
    }

    public string ToggleMute()
    {
        var current = CoreAudio.IsMuted();
        if (current is null)
        {
            NativeMethods.TapKey(NativeMethods.VK_VOLUME_MUTE);
            return Strings.Get("act.mutetoggled");
        }
        CoreAudio.SetMute(!current.Value);
        return Strings.Get(current.Value ? "act.unmuted" : "act.muted");
    }

    /// <summary>Current output level as 0-100, or null when there is no audio endpoint.</summary>
    public int? GetVolumePercent()
    {
        var level = CoreAudio.GetVolume();
        return level is null ? null : (int)Math.Round(level.Value * 100);
    }

    public bool? IsMuted() => CoreAudio.IsMuted();

    public string MediaPlayPause()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
        return Strings.Get("act.playpause");
    }

    public string MediaNext()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
        return Strings.Get("act.nexttrack");
    }

    public string MediaPrevious()
    {
        NativeMethods.TapKey(NativeMethods.VK_MEDIA_PREV_TRACK);
        return Strings.Get("act.prevtrack");
    }

    public string KillProcess(string nameOrPid)
    {
        nameOrPid = nameOrPid.Trim();
        if (string.IsNullOrEmpty(nameOrPid))
            throw new ArgumentException(Strings.Get("act.killneedsname"));

        if (int.TryParse(nameOrPid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            // A Process handle is an OS handle; scanning the table without disposing
            // leaks one per call, and this is reachable from a button.
            using var proc = ProcessById(pid);
            if (proc is null)
                return Strings.Format("act.killnone", TextUtil.Html(nameOrPid));
            var name = proc.ProcessName;
            proc.Kill(true);
            return Strings.Format("act.killedpid", name, pid);
        }

        var trimmed = nameOrPid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? nameOrPid[..^4] : nameOrPid;
        var matches = Process.GetProcessesByName(trimmed);
        try
        {
            if (matches.Length == 0)
                return Strings.Format("act.killnone", TextUtil.Html(nameOrPid));
            var killed = 0;
            foreach (var p in matches)
            {
                try { p.Kill(true); killed++; }
                catch (Exception ex) { _log.Warning($"Could not kill PID {p.Id}: {ex.Message}"); }
            }
            return Strings.Format("act.killedname", killed, TextUtil.Html(trimmed));
        }
        finally
        {
            foreach (var p in matches)
                p.Dispose();
        }
    }

    /// <summary>A process by id, or null when nothing is running under it any more.</summary>
    private static Process? ProcessById(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
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
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ShellTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* already gone */ }
            // A genuine stop is not a timeout: let the caller unwind.
            if (ct.IsCancellationRequested)
                throw;
            return Strings.Format("act.shelltimeout", ShellTimeoutSeconds);
        }

        var output = (await stdOutTask.ConfigureAwait(false)) + (await stdErrTask.ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(output))
            output = Strings.Format("act.shellnooutput", proc.ExitCode);
        return output;
    }

    public string GetClipboardText()
    {
        // The clipboard is STA-only, so every access is marshalled to the UI thread.
        var text = OnUiThread(() => Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
        return string.IsNullOrEmpty(text) ? Strings.Get("bot.clipboard.empty") : text;
    }

    public string SetClipboardText(string text)
    {
        OnUiThread(() =>
        {
            Clipboard.SetText(text);
            return true;
        });
        return Strings.Get("act.clipboardset");
    }

    public string OpenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException(Strings.Get("act.openneedstarget"));
        using var started = Process.Start(new ProcessStartInfo(target.Trim()) { UseShellExecute = true });
        return Strings.Format("act.opened", target.Trim());
    }

    public string TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException(Strings.Get("act.typeneedstext"));
        // SendKeys treats these as command characters, so they are escaped to literals.
        // Line breaks and tabs must become explicit key names: an unescaped '\n' resolves
        // to Ctrl+J, which is a live shortcut in browsers and editors.
        var escaped = new StringBuilder();
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            switch (c)
            {
                case '\n': escaped.Append("{ENTER}"); continue;
                case '\r': continue;
                case '\t': escaped.Append("{TAB}"); continue;
            }
            escaped.Append(c is '+' or '^' or '%' or '~' or '(' or ')' or '{' or '}' or '[' or ']'
                ? "{" + c + "}"
                : c.ToString());
        }

        OnUiThread(() =>
        {
            System.Windows.Forms.SendKeys.SendWait(escaped.ToString());
            return true;
        });
        return Strings.Format("act.typed", text.Length);
    }

    public async Task<string> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException(Strings.Get("act.speakneedstext"));
        // Long text would hold the single-threaded poll loop for minutes.
        if (text.Length > 500)
            text = text[..500];

        // Uses the speech synthesiser already present on Windows, so no extra dependency.
        // The text travels as base64 so no quote character can escape the script literal.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(text));
        var script = "Add-Type -AssemblyName System.Speech; " +
                     "(New-Object System.Speech.Synthesis.SpeechSynthesizer)" +
                     ".Speak([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')))";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never leave the synthesiser talking after we stop waiting for it.
            try { proc.Kill(true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested)
                throw;
            return Strings.Format("act.speaktimeout", ShellTimeoutSeconds);
        }

        var err = await errTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? Strings.Get("act.speakfailed") : err.Trim());
        return Strings.Get("act.spoken");
    }

    private static T OnUiThread<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return action();
        return dispatcher.Invoke(action);
    }

    private string CurrentVolumeText()
    {
        var percent = GetVolumePercent();
        return percent is null ? string.Empty : Strings.Format("act.volumenow", percent.Value);
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

        // Both pipes must be drained. Reading only stderr leaves stdout's buffer to
        // fill, at which point the child blocks writing and WaitForExit never returns.
        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* already gone */ }
            throw new InvalidOperationException(Strings.Format("act.processexit", file, "timeout"));
        }

        var err = await errTask.ConfigureAwait(false);
        await outTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            _log.Warning($"{file} {args} exited {proc.ExitCode}: {err}");
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(err)
                    ? Strings.Format("act.processexit", file, proc.ExitCode)
                    : err.Trim());
        }
        return successMessage;
    }
}
