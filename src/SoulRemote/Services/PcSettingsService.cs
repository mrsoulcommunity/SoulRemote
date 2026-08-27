using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using SoulRemote.Localization;
using SoulRemote.Services.Native;
using Windows.Devices.Radios;

namespace SoulRemote.Services;

/// <summary>
/// Windows' own settings, read and written the ways that work for a standard user.
///
/// That constraint is the whole shape of this file. Soul Remote runs asInvoker so it
/// can start silently at sign-in, which rules out the obvious implementation of half
/// of these: disabling a network adapter needs elevation, and a button that always
/// answers "requires elevation" is worse than no button at all. So Wi-Fi is driven
/// through <c>netsh wlan</c> — connect and disconnect, both per-user — rather than by
/// taking the adapter down, and the power plan through <c>powercfg /setactive</c>,
/// which sets it for the signed-in user.
/// </summary>
public sealed class PcSettingsService : IPcSettingsService
{
    /// <summary>
    /// powercfg and netsh answer in well under a second. This is short on purpose:
    /// these run inside a chat's command queue, and a tool that has wedged should give
    /// up long before the person waiting on it decides the bot is dead.
    /// </summary>
    private const int ToolTimeoutSeconds = 10;

    /// <summary>
    /// Matches a power scheme by its GUID and the parenthesised name, with a trailing
    /// "*" on the active one. Keyed on punctuation rather than on the words around it:
    /// "Power Scheme GUID" is translated, so a parser looking for that phrase would
    /// find nothing on a Persian or German Windows and report no plans at all.
    /// </summary>
    private static readonly Regex PlanLine = new(
        @"([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\s*\(([^)]*)\)(\s*\*)?",
        RegexOptions.Compiled);

    private readonly ILogService _log;

    /// <summary>
    /// The network this service last saw in use. Kept because once the link is down
    /// Windows no longer reports what it was, and the saved-profile list does not say
    /// which of them was the live one. In memory only; a restart may forget it.
    /// </summary>
    private string? _lastProfile;

    public PcSettingsService(ILogService log) => _log = log;

    // ---- power plans ----

    public async Task<IReadOnlyList<PowerPlan>> GetPowerPlansAsync(CancellationToken ct = default)
    {
        var run = await ProcessRunner.RunAsync("powercfg.exe", new[] { "/list" }, ToolTimeoutSeconds, ct)
            .ConfigureAwait(false);
        if (!run.Ok)
        {
            _log.Warning($"powercfg /list exited {run.ExitCode}: {run.StdErr.Trim()}");
            return Array.Empty<PowerPlan>();
        }

        var plans = new List<PowerPlan>();
        foreach (Match match in PlanLine.Matches(run.StdOut))
        {
            plans.Add(new PowerPlan(
                match.Groups[1].Value,
                match.Groups[2].Value.Trim(),
                match.Groups[3].Success));
        }
        return plans;
    }

    public async Task<string> SetPowerPlanAsync(string planId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(planId, out var guid))
            throw new InvalidOperationException(Strings.Get("act.plan.unknown"));

        // Looked up rather than trusted, so the reply can name the plan and an id that
        // is well-formed but not on this machine is refused before powercfg sees it.
        var plans = await GetPowerPlansAsync(ct).ConfigureAwait(false);
        var plan = plans.FirstOrDefault(p => Guid.TryParse(p.Id, out var id) && id == guid)
                   ?? throw new InvalidOperationException(Strings.Get("act.plan.unknown"));

        var run = await ProcessRunner.RunAsync(
            "powercfg.exe",
            new[] { "/setactive", guid.ToString("D", CultureInfo.InvariantCulture) },
            ToolTimeoutSeconds, ct).ConfigureAwait(false);
        if (!run.Ok)
        {
            _log.Warning($"powercfg /setactive exited {run.ExitCode}: {run.StdErr.Trim()}");
            throw new InvalidOperationException(Strings.Get("act.plan.failed"));
        }
        return Strings.Format("act.plan.set", plan.Name);
    }

    // ---- brightness ----

    /// <summary>
    /// Reads the panel brightness over WMI. This reaches the brightness a display
    /// driver exposes, which in practice means a laptop's built-in screen: an external
    /// monitor is driven over DDC/CI instead and does not appear here. On a desktop the
    /// class exists but has no instances, which is reported as "not supported" rather
    /// than as a failure, because nothing has gone wrong.
    /// </summary>
    public BrightnessState GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (var instance in results)
            {
                using (instance)
                {
                    if (instance["CurrentBrightness"] is { } value)
                        return new BrightnessState(true, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Brightness is not readable on this machine: {ex.Message}");
        }
        return new BrightnessState(false, null);
    }

    public string SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            var applied = false;
            foreach (var instance in results)
            {
                using var monitor = (ManagementObject)instance;
                // Timeout 1: the panel gets a second to reach the new level, and the
                // call returns rather than holding the chat's queue open behind it.
                monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                applied = true;
            }
            if (!applied)
                throw new InvalidOperationException(Strings.Get("act.bri.unsupported"));
            return Strings.Format("act.bri.set", percent);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _log.Warning($"Could not set brightness: {ex.Message}");
            throw new InvalidOperationException(Strings.Get("act.bri.failed"));
        }
    }

    // ---- Wi-Fi ----

    public async Task<WifiState> GetWifiAsync(CancellationToken ct = default)
    {
        // Adapter presence and link state come from the BCL rather than from netsh:
        // they are the two facts this screen must get right, and this way they do not
        // depend on parsing output whose labels are translated.
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToArray();
        if (adapters.Length == 0)
            return new WifiState(false, false, null);

        var connected = adapters.Any(a => a.OperationalStatus == OperationalStatus.Up);
        var profile = connected ? await ReadConnectedProfileAsync(ct).ConfigureAwait(false) : null;
        if (profile is { Length: > 0 })
            _lastProfile = profile;

        return new WifiState(true, connected, profile ?? (connected ? null : _lastProfile));
    }

    /// <summary>
    /// The name of the network in use, or null when it could not be read.
    ///
    /// Best-effort by design: netsh labels its output in the display language, so this
    /// recognises the English label and otherwise gives up. Giving up is survivable —
    /// the screen says "connected" without naming the network — whereas guessing at
    /// which colon-separated line holds the SSID would sometimes name the wrong one.
    /// </summary>
    private async Task<string?> ReadConnectedProfileAsync(CancellationToken ct)
    {
        try
        {
            var run = await ProcessRunner.RunAsync(
                "netsh.exe", new[] { "wlan", "show", "interfaces" }, ToolTimeoutSeconds, ct).ConfigureAwait(false);
            if (!run.Ok)
                return null;

            foreach (var line in run.StdOut.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                    continue;
                // "BSSID" also ends in SSID and holds a MAC address, so this matches
                // the whole label rather than looking for a substring.
                if (!line[..idx].Trim().Equals("SSID", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = line[(idx + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Debug($"Could not read the connected Wi-Fi profile: {ex.Message}");
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> GetWifiProfilesAsync(CancellationToken ct = default)
    {
        var run = await ProcessRunner.RunAsync(
            "netsh.exe", new[] { "wlan", "show", "profiles" }, ToolTimeoutSeconds, ct).ConfigureAwait(false);
        if (!run.Ok)
            return Array.Empty<string>();

        // Every profile arrives as "<translated label> : <name>". The label cannot be
        // relied on, but the shape can: an indented line with a colon and something
        // after it. Section headings ("Profiles on interface Wi-Fi:") end at the colon
        // and are not indented, so they fall out on their own.
        var names = new List<string>();
        foreach (var line in run.StdOut.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0 || line.Length == 0 || !char.IsWhiteSpace(line[0]))
                continue;
            var name = line[(idx + 1)..].Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        return names;
    }

    public async Task<string> ConnectWifiAsync(string profileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new InvalidOperationException(Strings.Get("act.wifi.noprofile"));

        var run = await ProcessRunner.RunAsync(
            "netsh.exe", new[] { "wlan", "connect", "name=" + profileName }, ToolTimeoutSeconds, ct)
            .ConfigureAwait(false);
        if (!run.Ok)
        {
            _log.Warning($"netsh wlan connect exited {run.ExitCode}: {run.StdOut.Trim()} {run.StdErr.Trim()}");
            throw new InvalidOperationException(Strings.Get("act.wifi.failed"));
        }
        _lastProfile = profileName;
        // Association takes a moment and netsh returns before it completes, so this
        // says what was started rather than claiming the link is already up.
        return Strings.Format("act.wifi.connecting", profileName);
    }

    public async Task<string> DisconnectWifiAsync(CancellationToken ct = default)
    {
        // Read first: once the link is down there is no way to find out what it was,
        // and the list this leaves behind is what the user reconnects from.
        _lastProfile = await ReadConnectedProfileAsync(ct).ConfigureAwait(false) ?? _lastProfile;

        var run = await ProcessRunner.RunAsync(
            "netsh.exe", new[] { "wlan", "disconnect" }, ToolTimeoutSeconds, ct).ConfigureAwait(false);
        if (!run.Ok)
        {
            _log.Warning($"netsh wlan disconnect exited {run.ExitCode}: {run.StdErr.Trim()}");
            throw new InvalidOperationException(Strings.Get("act.wifi.failed"));
        }
        return Strings.Get("act.wifi.disconnected");
    }

    // ---- Bluetooth ----

    public async Task<RadioPower> GetBluetoothAsync(CancellationToken ct = default)
    {
        try
        {
            var radio = await FindBluetoothRadioAsync(ct).ConfigureAwait(false);
            if (radio is null)
                return RadioPower.Unavailable;
            return radio.State == RadioState.On ? RadioPower.On : RadioPower.Off;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Reading is allowed to come back empty-handed; the screen says the radio
            // is out of reach, which is all the reader needs to know.
            _log.Debug($"Bluetooth state is not readable: {ex.Message}");
            return RadioPower.Unavailable;
        }
    }

    public async Task<string> SetBluetoothAsync(bool on, CancellationToken ct = default)
    {
        var radio = await FindBluetoothRadioAsync(ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(Strings.Get("act.bt.none"));

        RadioAccessStatus status;
        try
        {
            status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off).AsTask(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning($"Could not change the Bluetooth radio: {ex.Message}");
            throw new InvalidOperationException(Strings.Get("act.bt.failed"));
        }

        if (status != RadioAccessStatus.Allowed)
            throw new InvalidOperationException(Strings.Get("act.bt.failed"));
        return Strings.Get(on ? "act.bt.on" : "act.bt.off");
    }

    /// <summary>
    /// The Bluetooth radio, or null when this machine has none. Access is requested
    /// first, and a refusal is raised as its own message: "Windows said no" and "there
    /// is no radio here" are different situations, and telling someone to check their
    /// hardware when the real answer is a privacy setting wastes their afternoon.
    /// </summary>
    private static async Task<Radio?> FindBluetoothRadioAsync(CancellationToken ct)
    {
        var access = await Radio.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
        if (access != RadioAccessStatus.Allowed)
            throw new InvalidOperationException(Strings.Get("act.bt.denied"));

        var radios = await Radio.GetRadiosAsync().AsTask(ct).ConfigureAwait(false);
        return radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
    }
}
