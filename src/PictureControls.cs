using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// The camera's standard picture controls (brightness, white balance, exposure...). They're stored
/// in the camera itself, so they work in every app. Reached through the open camera source.
/// </summary>
public static class PictureControls
{
    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int prop, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int prop, int value, int flags);
        [PreserveSig] int Get(int prop, out int value, out int flags);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int prop, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int prop, int value, int flags);
        [PreserveSig] int Get(int prop, out int value, out int flags);
    }

    const int Auto = 1, Manual = 2;

    // name -> (camera control?, property id)
    public static readonly (string name, bool cam, int id)[] All =
    [
        ("Brightness", false, 0), ("Contrast", false, 1), ("Saturation", false, 3), ("Sharpness", false, 4),
        ("Gamma", false, 5), ("White balance", false, 7), ("Backlight", false, 8), ("Gain", false, 9),
        ("Exposure", true, 4), ("Focus", true, 6),
    ];

    public sealed record Info(int min, int max, int step, int @default, int value, bool auto, bool can_auto);

    public static Dictionary<string, Info> Read(Capture cap)
    {
        var d = new Dictionary<string, Info>();
        var src = Marshal.GetObjectForIUnknown(cap.Source.NativePointer);
        foreach (var (name, cam, id) in All)
        {
            try
            {
                int mn, mx, st, df, caps, v, fl, hr;
                if (cam) { var i = (IAMCameraControl)src; hr = i.GetRange(id, out mn, out mx, out st, out df, out caps); if (hr != 0) continue; i.Get(id, out v, out fl); }
                else { var i = (IAMVideoProcAmp)src; hr = i.GetRange(id, out mn, out mx, out st, out df, out caps); if (hr != 0) continue; i.Get(id, out v, out fl); }
                if (mx <= mn) continue;
                d[name] = new Info(mn, mx, Math.Max(1, st), df, v, (fl & Auto) != 0, (caps & Auto) != 0);
            }
            catch { }
        }
        return d;
    }

    public static void Set(Capture cap, string name, int value, bool auto)
    {
        var (_, cam, id) = All.First(a => a.name == name);
        var src = Marshal.GetObjectForIUnknown(cap.Source.NativePointer);
        int flags = auto ? Auto : Manual;
        if (cam) ((IAMCameraControl)src).Set(id, value, flags); else ((IAMVideoProcAmp)src).Set(id, value, flags);
    }

    public static Info? Reset(Capture cap, string name)
    {
        var all = Read(cap);
        if (!all.TryGetValue(name, out var i)) return null;
        Set(cap, name, i.@default, i.can_auto);
        return Read(cap).GetValueOrDefault(name);
    }
}
