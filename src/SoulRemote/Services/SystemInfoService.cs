using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinFormsSysInfo = System.Windows.Forms.SystemInformation;

namespace SoulRemote.Services;

public interface ISystemInfoService
{
    Task<string> GetSystemInfoAsync(CancellationToken ct = default);
    string GetDisks();
    string GetBattery();
    string GetTopProcesses(int count = 12);
    Task<string> GetNetworkAsync(CancellationToken ct = default);
}

public sealed class SystemInfoService : ISystemInfoService
{
    private readonly ILogService _log;
    private readonly HttpClient _http;

    public SystemInfoService(ILogService log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulRemote/1.0");
    }

    public async Task<string> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🖥 System Information</b>");
        sb.AppendLine();
        sb.AppendLine($"<b>Machine:</b> {TextUtil.Html(Environment.MachineName)}");
        sb.AppendLine($"<b>User:</b> {TextUtil.Html(Environment.UserName)}");
        sb.AppendLine($"<b>OS:</b> {TextUtil.Html(RuntimeInformation.OSDescription)}");
        sb.AppendLine($"<b>Architecture:</b> {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"<b>CPU:</b> {TextUtil.Html(GetCpuName())} ({Environment.ProcessorCount} logical cores)");

        var mem = GetMemory();
        if (mem is not null)
        {
            var (totalKb, availKb) = mem.Value;
            var usedPct = totalKb == 0 ? 0 : Math.Round((totalKb - availKb) * 100.0 / totalKb);
            sb.AppendLine($"<b>RAM:</b> {TextUtil.HumanBytes((totalKb - availKb) * 1024)} / {TextUtil.HumanBytes(totalKb * 1024)} used ({usedPct}%)");
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        sb.AppendLine($"<b>Uptime:</b> {TextUtil.HumanDuration(uptime)}");
        sb.AppendLine($"<b>Local time:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        await Task.CompletedTask.ConfigureAwait(false);
        return sb.ToString();
    }

    public string GetDisks()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>💽 Disks</b>");
        sb.AppendLine();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var used = d.TotalSize - d.TotalFreeSpace;
                var pct = d.TotalSize == 0 ? 0 : Math.Round(used * 100.0 / d.TotalSize);
                sb.AppendLine($"<b>{TextUtil.Html(d.Name)}</b> [{TextUtil.Html(d.DriveType.ToString())}] " +
                              $"{TextUtil.HumanBytes(used)} / {TextUtil.HumanBytes(d.TotalSize)} ({pct}%), " +
                              $"free {TextUtil.HumanBytes(d.TotalFreeSpace)}");
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
            sb.AppendLine("<b>🔋 Power</b>");
            sb.AppendLine();
            sb.AppendLine($"<b>Line:</b> {ps.PowerLineStatus}");
            if (ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            {
                sb.AppendLine("<b>Battery:</b> none (desktop).");
            }
            else
            {
                var pct = ps.BatteryLifePercent; // 0..1 or 255 if unknown
                if (pct >= 0 && pct <= 1)
                    sb.AppendLine($"<b>Battery:</b> {Math.Round(pct * 100)}% ({ps.BatteryChargeStatus})");
                else
                    sb.AppendLine($"<b>Battery:</b> {ps.BatteryChargeStatus}");
                if (ps.BatteryLifeRemaining > 0)
                    sb.AppendLine($"<b>Remaining:</b> {TextUtil.HumanDuration(TimeSpan.FromSeconds(ps.BatteryLifeRemaining))}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Power status unavailable: {TextUtil.Html(ex.Message)}";
        }
    }

    public string GetTopProcesses(int count = 12)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>📋 Top {count} processes by memory</b>");
        sb.AppendLine();
        var procs = Process.GetProcesses()
            .Select(p =>
            {
                try { return (p.ProcessName, p.Id, p.WorkingSet64); }
                catch { return (p.ProcessName, p.Id, 0L); }
            })
            .OrderByDescending(x => x.Item3)
            .Take(count);
        foreach (var (name, id, ws) in procs)
            sb.AppendLine($"{TextUtil.Html(name)} (PID {id}) — {TextUtil.HumanBytes(ws)}");
        return sb.ToString();
    }

    public async Task<string> GetNetworkAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🌐 Network</b>");
        sb.AppendLine();
        sb.AppendLine($"<b>Host:</b> {TextUtil.Html(Dns.GetHostName())}");

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
            sb.AppendLine($"<b>Local IPs:</b> {(locals.Count > 0 ? string.Join(", ", locals) : "none")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"<b>Local IPs:</b> error ({TextUtil.Html(ex.Message)})");
        }

        try
        {
            var publicIp = await _http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false);
            sb.AppendLine($"<b>Public IP:</b> {TextUtil.Html(publicIp.Trim())}");
        }
        catch
        {
            sb.AppendLine("<b>Public IP:</b> unavailable");
        }

        return sb.ToString();
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
