using System.Runtime.InteropServices;

namespace Noch;

/// How loud the default speakers are right now, 0 to 1. Reads the level only, never the sound itself.
static class AudioMeter
{
    static IAudioMeterInformation? _meter;
    static DateTime _since;

    public static float Peak()
    {
        try
        {
            // Look the device up again now and then, in case headphones were plugged in.
            if (_meter is null || DateTime.UtcNow - _since > TimeSpan.FromSeconds(5))
            {
                var e = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                e.GetDefaultAudioEndpoint(0, 1, out var device);
                var iid = typeof(IAudioMeterInformation).GUID;
                device.Activate(ref iid, 1, IntPtr.Zero, out var o);
                _meter = (IAudioMeterInformation)o;
                _since = DateTime.UtcNow;
            }
            _meter.GetPeakValue(out var p);
            return p;
        }
        catch { _meter = null; return 0; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, int state, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
