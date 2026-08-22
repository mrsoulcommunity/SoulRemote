using System.Runtime.InteropServices;

namespace SoulRemote.Services.Native;

/// <summary>Win32 P/Invoke declarations used for system control.</summary>
internal static class NativeMethods
{
    // ---- Workstation lock ----
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();

    // ---- Sleep / hibernate ----
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    // ---- Media / volume keys via synthesized keystrokes ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const byte VK_VOLUME_MUTE = 0xAD;
    internal const byte VK_VOLUME_DOWN = 0xAE;
    internal const byte VK_VOLUME_UP = 0xAF;
    internal const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    internal const byte VK_MEDIA_PREV_TRACK = 0xB1;
    internal const byte VK_MEDIA_STOP = 0xB2;
    internal const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    internal static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- Monitor power (turn display off) ----
    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const int SC_MONITORPOWER = 0xF170;
    internal const int MONITOR_OFF = 2;
    internal static readonly IntPtr HWND_BROADCAST = new(0xffff);
}
