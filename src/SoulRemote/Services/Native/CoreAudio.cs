using System.Runtime.InteropServices;

namespace SoulRemote.Services.Native;

/// <summary>
/// Minimal Core Audio (MMDevice / IAudioEndpointVolume) COM interop for reading
/// and setting the default playback device's master volume as a 0..1 scalar.
/// </summary>
internal static class CoreAudio
{
    public static float? GetVolume()
    {
        var vol = GetEndpointVolume();
        if (vol is null) return null;
        try
        {
            Marshal.ThrowExceptionForHR(vol.GetMasterVolumeLevelScalar(out var level));
            return level;
        }
        finally
        {
            Marshal.ReleaseComObject(vol);
        }
    }

    public static bool SetVolume(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var vol = GetEndpointVolume();
        if (vol is null) return false;
        try
        {
            var guid = Guid.Empty;
            Marshal.ThrowExceptionForHR(vol.SetMasterVolumeLevelScalar(level, ref guid));
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(vol);
        }
    }

    public static bool? IsMuted()
    {
        var vol = GetEndpointVolume();
        if (vol is null) return null;
        try
        {
            Marshal.ThrowExceptionForHR(vol.GetMute(out var muted));
            return muted;
        }
        finally
        {
            Marshal.ReleaseComObject(vol);
        }
    }

    public static bool SetMute(bool mute)
    {
        var vol = GetEndpointVolume();
        if (vol is null) return false;
        try
        {
            var guid = Guid.Empty;
            Marshal.ThrowExceptionForHR(vol.SetMute(mute, ref guid));
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(vol);
        }
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out var obj));
            return (IAudioEndpointVolume)obj;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    // ---- COM definitions ----

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [Flags]
    private enum CLSCTX : uint { ALL = 0x17 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        // Remaining vtable entries are not needed.
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        // Remaining vtable entries are not needed.
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
        [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }
}
