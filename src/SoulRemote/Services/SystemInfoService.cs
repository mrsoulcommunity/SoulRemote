using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SoulRemote.Localization;
using WinFormsSysInfo = System.Windows.Forms.SystemInformation;

namespace SoulRemote.Services;

public sealed class SystemInfoService : ISystemInfoService
{
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private readonly ICloudflareService _cloudflare;

    // CPU load is a rate, not a reading: it only exists as the difference between two
    // samples. The previous one is kept so the first /sysinfo after startup still has
    // something to compare against.
    private readonly object _cpuLock = new();
    private (ulong Idle, ulong Total)? _lastCpuSample;

    public SystemInfoService(ILogService log, ISettingsService settings, ICloudflareService cloudflare)
    {
        _log = log;
        _settings = settings;
        _cloudflare = cloudflare;
    }

    public async Task<string> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Get("sys.info.title"));
        sb.AppendLine();
        TextUtil.AppendField(sb, Strings.Get("sys.info.machine"), TextUtil.Html(Environment.MachineName));
        TextUtil.AppendField(sb, Strings.Get("sys.info.user"), TextUtil.Html(Environment.UserName));
        TextUtil.AppendField(sb, Strings.Get("sys.info.os"), TextUtil.Html(RuntimeInformation.OSDescription));
        TextUtil.AppendField(sb, Strings.Get("sys.info.arch"), RuntimeInformation.OSArchitecture.ToString());
        TextUtil.AppendField(sb, Strings.Get("sys.info.cpu"),
            $"{TextUtil.Html(GetCpuName())} ({Strings.Format("sys.info.cores", Environment.ProcessorCount)})");

        if (await SampleCpuLoadAsync(ct).ConfigureAwait(false) is { } load)
            TextUtil.AppendField(sb, Strings.Get("sys.info.load"), $"{load}%");

        var mem = GetMemory();
        if (mem is not null)
        {
            var (totalKb, availKb) = mem.Value;
            var usedPct = totalKb == 0 ? 0 : Math.Round((totalKb - availKb) * 100.0 / totalKb);
            TextUtil.AppendField(sb, Strings.Get("sys.info.ram"), Strings.Format("sys.info.ramused",
                TextUtil.HumanBytes((totalKb - availKb) * 1024), TextUtil.HumanBytes(totalKb * 1024), usedPct));
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        TextUtil.AppendField(sb, Strings.Get("sys.info.uptime"), TextUtil.HumanDuration(uptime));
        TextUtil.AppendField(sb, Strings.Get("sys.info.localtime"),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    public string GetDisks()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Get("sys.disks.title"));
        sb.AppendLine();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            // Enumerating drives can itself throw (a disconnected network drive, an
            // I/O fault). Report it rather than letting the whole reply disappear.
            return sb.AppendLine(Strings.Format("sys.net.error", TextUtil.Html(ex.Message))).ToString();
        }

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) continue;
                var used = d.TotalSize - d.TotalFreeSpace;
                var pct = d.TotalSize == 0 ? 0 : Math.Round(used * 100.0 / d.TotalSize);
                sb.AppendLine($"<b>{TextUtil.Html(d.Name)}</b> [{TextUtil.Html(d.DriveType.ToString())}] " +
                              $"{TextUtil.HumanBytes(used)} / {TextUtil.HumanBytes(d.TotalSize)} ({pct}%), " +
                              Strings.Format("sys.disks.free", TextUtil.HumanBytes(d.TotalFreeSpace)));
            }
            catch (Exception ex)
            {
                _log.Debug($"Drive {d.Name}: {ex.Message}");
            }
        }
        return sb.ToString();
    }

    public string GetBattery()
    {
        try
        {
            var ps = WinFormsSysInfo.PowerStatus;
            var sb = new StringBuilder();
            sb.AppendLine(Strings.Get("sys.power.title"));
            sb.AppendLine();
            TextUtil.AppendField(sb, Strings.Get("sys.power.line"), ps.PowerLineStatus.ToString());
            if (ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            {
                TextUtil.AppendField(sb, Strings.Get("sys.power.battery"), Strings.Get("sys.power.nobattery"));
            }
            else
            {
                var pct = ps.BatteryLifePercent; // 0..1 or 255 if unknown
                TextUtil.AppendField(sb, Strings.Get("sys.power.battery"),
                    pct is >= 0 and <= 1
                        ? $"{Math.Round(pct * 100)}% ({ps.BatteryChargeStatus})"
                        : ps.BatteryChargeStatus.ToString());
                if (ps.BatteryLifeRemaining > 0)
                    TextUtil.AppendField(sb, Strings.Get("sys.power.remaining"),
                        TextUtil.HumanDuration(TimeSpan.FromSeconds(ps.BatteryLifeRemaining)));
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return Strings.Format("sys.power.unavailable", TextUtil.Html(ex.Message));
        }
    }

    public string GetTopProcesses(int count = 12)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Format("sys.proc.title", count));
        sb.AppendLine();

        // Every Process here holds an OS handle. Without the dispose pass this leaks
        // one per running process on every call, and this one is on a button.
        var all = Process.GetProcesses();
        try
        {
            var top = all
                .Select(p =>
                {
                    try { return (p.ProcessName, p.Id, Working: p.WorkingSet64); }
                    catch { return (p.ProcessName, p.Id, Working: 0L); }
                })
                .OrderByDescending(x => x.Working)
                .Take(count);
            foreach (var (name, id, ws) in top)
                sb.AppendLine($"{TextUtil.Html(name)} (PID {id}) — {TextUtil.HumanBytes(ws)}");
        }
        finally
        {
            foreach (var p in all)
                p.Dispose();
        }
        return sb.ToString();
    }

    public async Task<string> GetNetworkAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Get("sys.net.title"));
        sb.AppendLine();
        TextUtil.AppendField(sb, Strings.Get("sys.net.host"), TextUtil.Html(Dns.GetHostName()));

        try
        {
            var locals = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        locals.Add($"{addr.Address} ({TextUtil.Html(ni.Name)})");
                }
            }
            TextUtil.AppendField(sb, Strings.Get("sys.net.local"),
                locals.Count > 0 ? string.Join(", ", locals) : Strings.Get("sys.net.none"));
        }
        catch (Exception ex)
        {
            TextUtil.AppendField(sb, Strings.Get("sys.net.local"), Strings.Format("sys.net.error", TextUtil.Html(ex.Message)));
        }

        // The public address comes from the relay worker, which already sees it.
        // Asking a third-party lookup service would send a direct request over the
        // very network this app exists to avoid depending on.
        var settings = _settings.Current;
        var publicIp = await _cloudflare.GetPublicIpAsync(settings.WorkerUrl, settings.ProxySecret, ct)
            .ConfigureAwait(false);
        TextUtil.AppendField(sb, Strings.Get("sys.net.public"),
            publicIp is { Length: > 0 } ? TextUtil.Html(publicIp) : Strings.Get("sys.net.unavailable"));

        return sb.ToString();
    }

    /// <summary>
    /// CPU utilisation across all cores, as a percentage, from two samples of the
    /// kernel's own counters. Returns null on the very first call of a fresh process
    /// if no baseline could be taken.
    /// </summary>
    private async Task<int?> SampleCpuLoadAsync(CancellationToken ct)
    {
        try
        {
            var first = ReadCpuTimes();
            if (first is null)
                return null;

            (ulong Idle, ulong Total)? baseline;
            lock (_cpuLock)
                baseline = _lastCpuSample;

            // With no usable baseline, take one over a short window rather than
            // reporting nothing at all.
            if (baseline is null)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                baseline = first;
                first = ReadCpuTimes();
                if (first is null)
                    return null;
            }

            var idleDelta = first.Value.Idle - baseline.Value.Idle;
            var totalDelta = first.Value.Total - baseline.Value.Total;

            lock (_cpuLock)
                _lastCpuSample = first;

            if (totalDelta == 0)
                return null;
            var busy = 100.0 * (totalDelta - idleDelta) / totalDelta;
            return (int)Math.Round(Math.Clamp(busy, 0, 100));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Debug($"CPU load unavailable: {ex.Message}");
            return null;
        }
    }

    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);
        // Kernel time already includes idle time, so the total is kernel + user.
        return (idleTicks, kernelTicks + userTicks);
    }

    private static ulong ToTicks(FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
        }
        catch
        {
            return "Unknown CPU";
        }
    }

    // total KB, available KB
    private static (long total, long available)? GetMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return null;
        return ((long)(status.ullTotalPhys / 1024), (long)(status.ullAvailPhys / 1024));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
}
