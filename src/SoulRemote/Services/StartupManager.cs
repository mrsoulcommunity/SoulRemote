using System.Diagnostics;
using Microsoft.Win32;

namespace SoulRemote.Services;

public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>Manages the per-user "run at sign-in" registry entry.</summary>
public sealed class StartupManager : IStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoulRemote";
    private readonly ILogService _log;

    public StartupManager(ILogService log) => _log = log;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex)
        {
            _log.Debug($"Startup check failed: {ex.Message}");
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = GetExecutablePath();
                key.SetValue(ValueName, $"\"{exePath}\" --minimized");
                _log.Info("Enabled start with Windows.");
            }
            else
            {
                key.DeleteValue(ValueName, false);
                _log.Info("Disabled start with Windows.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to update startup setting", ex);
        }
    }

    private static string GetExecutablePath()
    {
        // Prefer the real .exe path (Environment.ProcessPath), fall back to the module file name.
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    }
}
