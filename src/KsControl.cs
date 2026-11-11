using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Low-level camera properties through the driver's IKsControl, for controls Windows' usual picture
/// interfaces don't expose - mainly UVC "auto-exposure priority": when on (1) the camera may slow
/// its frame rate to collect more light; off (0) it keeps full frame rate and uses gain instead.
/// </summary>
public static unsafe class KsControl
{
    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(void* property, uint propertyLength, void* data, uint dataLength, out uint returned);
        [PreserveSig] int KsMethod(void* method, uint methodLength, void* data, uint dataLength, out uint returned);
        [PreserveSig] int KsEvent(void* ev, uint eventLength, void* data, uint dataLength, out uint returned);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KsProp { public Guid Set; public uint Id; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct CameraControlS { public KsProp Property; public int Value; public uint Flags; public uint Capabilities; }

    static readonly Guid CameraControlSet = new("C6E13370-30AC-11d0-A18C-00A0C9118956");
    const uint Get = 1, Set = 2;
    public const uint AutoExposurePriority = 19;

    static IKsControl? Ks(Capture cap)
    {
        try { return (IKsControl)cap.Filter; }
        catch { return null; }
    }

    /// <summary>Read a camera-control property (null if the camera doesn't have it).</summary>
    public static int? GetCameraControl(Capture cap, uint id)
    {
        var ks = Ks(cap);
        if (ks == null) return null;
        var s = new CameraControlS { Property = new KsProp { Set = CameraControlSet, Id = id, Flags = Get } };
        int hr = ks.KsProperty(&s, (uint)sizeof(CameraControlS), &s, (uint)sizeof(CameraControlS), out _);
        return hr == 0 ? s.Value : null;
    }

    public static bool SetCameraControl(Capture cap, uint id, int value)
    {
        var ks = Ks(cap);
        if (ks == null) return false;
        var s = new CameraControlS
        {
            Property = new KsProp { Set = CameraControlSet, Id = id, Flags = Set },
            Value = value, Flags = 2 /* manual */,
        };
        return ks.KsProperty(&s, (uint)sizeof(CameraControlS), &s, (uint)sizeof(CameraControlS), out _) == 0;
    }
}
