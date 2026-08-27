using System.Diagnostics;

namespace SoulRemote.Services.Native;

/// <summary>
/// Runs a console tool and waits for it, without the two ways that usually goes
/// wrong: both pipes are drained, and a child that never exits is killed rather
/// than hanging the caller forever.
///
/// Draining matters more than it looks. Reading only stderr leaves stdout's buffer
/// to fill, at which point the child blocks on its next write and WaitForExit never
/// returns — a deadlock that only shows up on the commands that happen to be
/// chatty, which is exactly what powercfg and netsh are.
///
/// Arguments are passed as a list rather than a command line. Some of what gets
/// passed here is a Wi-Fi profile name, which is whatever the network was called,
/// and building a command line out of that by concatenation is how a space or a
/// quote turns into a second argument.
/// </summary>
internal static class ProcessRunner
{
    internal readonly record struct Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    internal static async Task<Result> RunAsync(
        string file, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* already gone */ }
            // The caller's own cancellation is a different thing from this tool
            // running long, and must keep propagating as cancellation.
            ct.ThrowIfCancellationRequested();
            return new Result(-1, string.Empty, "timeout");
        }

        return new Result(proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
