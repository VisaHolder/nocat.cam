using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NocatCam;

/// <summary>Just the DirectShow pieces nocat.cam needs, declared by hand (no extra library).</summary>
static class DShow
{
    public static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    public static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    public static readonly Guid CLSID_FilterGraph = new("e436ebb3-524f-11ce-9f53-0020af0ba770");
    public static readonly Guid CLSID_CaptureGraphBuilder2 = new("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
    public static readonly Guid CLSID_SampleGrabber = new("C1F400A0-3F08-11d3-9F0B-006008039E37");
    public static readonly Guid CLSID_NullRenderer = new("C1F400A4-3F08-11d3-9F0B-006008039E37");
    public static readonly Guid PIN_CATEGORY_CAPTURE = new("fb6c4281-0353-11d1-905f-0000c0cc16ba");
    public static readonly Guid MEDIATYPE_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MEDIASUBTYPE_MJPG = new("47504A4D-0000-0010-8000-00AA00389B71");
    public static readonly Guid FORMAT_VideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a");
    public static readonly Guid IID_IBaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");
    public static readonly Guid IID_IAMStreamConfig = new("C6E13340-30AC-11d0-A18C-00A0C9118956");

    [StructLayout(LayoutKind.Sequential)]
    public struct AMMediaType
    {
        public Guid majortype, subtype;
        public int fixedSizeSamples, temporalCompression;
        public int sampleSize;
        public Guid formattype;
        public nint pUnk;
        public int cbFormat;
        public nint pbFormat;
    }

    // VIDEOINFOHEADER offsets inside pbFormat
    public const int VihAvgTimePerFrame = 40, VihWidth = 52, VihHeight = 56;

    public static void Free(nint pmt)
    {
        if (pmt == 0) return;
        var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
        if (mt.pbFormat != 0) Marshal.FreeCoTaskMem(mt.pbFormat);
        if (mt.pUnk != 0) Marshal.Release(mt.pUnk);
        Marshal.FreeCoTaskMem(pmt);
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker? enumMoniker, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, out object val, nint errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object val);
    }

    /// <summary>Only passed around (as a real IBaseFilter pointer), never called.</summary>
    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBaseFilter { }

    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphBuilder
    {
        [PreserveSig] int AddFilter(IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int RemoveFilter(IBaseFilter filter);
    }

    [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICaptureGraphBuilder2
    {
        [PreserveSig] int SetFiltergraph(IGraphBuilder graph);
        [PreserveSig] int GetFiltergraph(out IGraphBuilder graph);
        [PreserveSig] int SetOutputFileName(nint type, nint file, nint mux, nint sink);
        [PreserveSig] int FindInterface([In] ref Guid category, [In] ref Guid type, IBaseFilter filter,
                                        [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object found);
        [PreserveSig] int RenderStream([In] ref Guid category, [In] ref Guid type, [MarshalAs(UnmanagedType.IUnknown)] object source,
                                       IBaseFilter? compressor, IBaseFilter? renderer);
    }

    [ComImport, Guid("C6E13340-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(nint pmt);
        [PreserveSig] int GetFormat(out nint pmt);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out nint pmt, nint scc);
    }

    [ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISampleGrabber
    {
        [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool oneShot);
        [PreserveSig] int SetMediaType(nint pmt);
        [PreserveSig] int GetConnectedMediaType(nint pmt);
        [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool buffer);
        [PreserveSig] int GetCurrentBuffer(ref int size, nint buffer);
        [PreserveSig] int GetCurrentSample(out nint sample);
        [PreserveSig] int SetCallback(ISampleGrabberCB callback, int whichMethod);
    }

    [ComImport, Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISampleGrabberCB
    {
        [PreserveSig] int SampleCB(double time, nint sample);
        [PreserveSig] int BufferCB(double time, nint buffer, int length);
    }

    [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int GetState(int timeoutMs, out int state);
    }

    public static T Create<T>(Guid clsid) => (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;

    /// <summary>Video cameras by name, each with its moniker (to create the capture filter).</summary>
    public static List<(string name, IMoniker moniker)> Cameras()
    {
        var list = new List<(string, IMoniker)>();
        var devEnum = Create<ICreateDevEnum>(CLSID_SystemDeviceEnum);
        var cat = CLSID_VideoInputDeviceCategory;
        if (devEnum.CreateClassEnumerator(ref cat, out var em, 0) != 0 || em == null) return list;
        var one = new IMoniker[1];
        while (em.Next(1, one, 0) == 0)
        {
            var m = one[0];
            var bagId = typeof(IPropertyBag).GUID;
            m.BindToStorage(null!, null, ref bagId, out var bagObj);
            string name = "?";
            if (bagObj is IPropertyBag bag && bag.Read("FriendlyName", out var v, 0) == 0) name = v as string ?? "?";
            list.Add((name, m));
        }
        return list;
    }
}

/// <summary>Receives each compressed picture from DirectShow's sample grabber (on its streaming thread).</summary>
[ComVisible(true)]
sealed class GrabberSink : DShow.ISampleGrabberCB
{
    readonly object _lock = new();
    readonly AutoResetEvent _arrived = new(false);
    byte[] _latest = new byte[1 << 20];
    int _len, _seq, _taken;
    public volatile bool Closed;
    /// <summary>Every picture the camera delivered (not just the ones taken).</summary>
    public int Arrived => Volatile.Read(ref _seq);

    public int SampleCB(double time, nint sample) => 0;

    public int BufferCB(double time, nint buffer, int length)
    {
        if (Closed || length <= 0) return 0;
        lock (_lock)
        {
            if (_latest.Length < length) _latest = new byte[length + length / 4];
            Marshal.Copy(buffer, _latest, 0, length);
            _len = length;
            _seq++;
        }
        _arrived.Set();
        return 0;
    }

    /// <summary>Newest picture not handed out yet: its size, 0 if none arrived within the wait, -1 if closed.</summary>
    public int Take(ref byte[] buf, bool wantData, int waitMs = 2000)
    {
        while (true)
        {
            if (Closed) return -1;
            lock (_lock)
            {
                if (_seq != _taken)
                {
                    _taken = _seq;
                    if (!wantData) return 1;
                    if (buf.Length < _len) buf = new byte[_len + _len / 4];
                    Buffer.BlockCopy(_latest, 0, buf, 0, _len);
                    return _len;
                }
            }
            if (!_arrived.WaitOne(waitMs)) return 0;
        }
    }
}
