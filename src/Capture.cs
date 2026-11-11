using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Reads the camera through DirectShow as its own compressed MJPEG pictures, at the exact size and
/// frame rate asked for (decoded by libjpeg-turbo in TurboJpeg.cs).
///
/// Why DirectShow: on the EMEET PIXY, Windows Media Foundation delivered half the frame rate
/// (1080p60 came in at 30, 4K30 at 20) whatever was tried, while DirectShow - like ffmpeg and
/// Discord - gets the full 60 / 30.
/// </summary>
public sealed class Capture : IDisposable
{
    readonly DShow.IMediaControl _control;
    readonly GrabberSink _sink;
    readonly object _graph, _builder, _grabber, _nullRenderer;
    public object Filter { get; }                        // the camera's capture filter (picture controls live on it)
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Native { get; }

    Capture(object graph, object builder, object filter, object grabber, object nullRenderer, GrabberSink sink,
            int w, int h, int fps)
    {
        _graph = graph; _builder = builder; Filter = filter; _grabber = grabber; _nullRenderer = nullRenderer;
        _control = (DShow.IMediaControl)graph; _sink = sink;
        Width = w; Height = h; Fps = fps; Native = $"MJPEG {w}x{h}@{fps}";
    }

    public static void Startup() { }

    /// <summary>Names of the video cameras Windows knows about.</summary>
    public static List<string> List()
    {
        try { return DShow.Cameras().Select(c => c.name).ToList(); }
        catch { return []; }
    }

    /// <summary>Every MJPEG size/frame rate the camera offers (for troubleshooting).</summary>
    /// <summary>
    /// The PIXY family: "EMEET PIXY" (4K) and the PIXY 2K may show up under slightly different names, so
    /// asking for "EMEET PIXY" takes an exact match first, else any camera with PIXY in its name
    /// (never a virtual camera).
    /// </summary>
    static (string name, System.Runtime.InteropServices.ComTypes.IMoniker? moniker) Find(string name)
    {
        var cams = DShow.Cameras();
        var exact = cams.FirstOrDefault(c => c.name == name);
        if (exact.moniker != null || name != "EMEET PIXY") return exact;
        return cams.FirstOrDefault(c => c.name.Contains("PIXY", StringComparison.OrdinalIgnoreCase)
                                        && !c.name.Contains("virtual", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPixy(string cameraName) =>
        cameraName == "EMEET PIXY" || (cameraName.Contains("PIXY", StringComparison.OrdinalIgnoreCase) && !cameraName.Contains("virtual", StringComparison.OrdinalIgnoreCase));

    /// <summary>Picture sizes the camera offered the last time it was opened (e.g. 3840x2160, 2560x1440...).</summary>
    public static volatile string[] LastSizes = [];

    public static List<string> Modes(string name)
    {
        var list = new List<string>();
        var cam = Find(name);
        if (cam.moniker == null) return list;
        try
        {
            var filter = Bind(cam.moniker);
            var graph = DShow.Create<DShow.IGraphBuilder>(DShow.CLSID_FilterGraph);
            graph.AddFilter((DShow.IBaseFilter)filter, "camera");
            var builder = DShow.Create<DShow.ICaptureGraphBuilder2>(DShow.CLSID_CaptureGraphBuilder2);
            builder.SetFiltergraph(graph);
            var sc = StreamConfig(builder, filter);
            foreach (var (w, h, fps, _) in Caps(sc)) list.Add($"MJPEG {w}x{h} up to {fps:0.##} fps");
            Marshal.ReleaseComObject(graph);
        }
        catch (Exception e) { list.Add("couldn't list: " + e.Message); }
        return list;
    }

    static object Bind(System.Runtime.InteropServices.ComTypes.IMoniker m)
    {
        var iid = DShow.IID_IBaseFilter;
        m.BindToObject(null!, null, ref iid, out var obj);
        return obj;
    }

    static DShow.IAMStreamConfig StreamConfig(DShow.ICaptureGraphBuilder2 builder, object filter)
    {
        var cat = DShow.PIN_CATEGORY_CAPTURE; var type = DShow.MEDIATYPE_Video; var iid = DShow.IID_IAMStreamConfig;
        int hr = builder.FindInterface(ref cat, ref type, (DShow.IBaseFilter)filter, ref iid, out var sc);
        if (hr != 0) throw new InvalidOperationException($"no format control (0x{hr:X8})");
        return (DShow.IAMStreamConfig)sc;
    }

    /// <summary>The camera's MJPEG formats: (width, height, max fps, index for GetStreamCaps).</summary>
    static List<(int w, int h, double fps, int index)> Caps(DShow.IAMStreamConfig sc)
    {
        var list = new List<(int, int, double, int)>();
        sc.GetNumberOfCapabilities(out int count, out int size);
        nint scc = Marshal.AllocCoTaskMem(Math.Max(size, 256));
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (sc.GetStreamCaps(i, out var pmt, scc) != 0) continue;
                try
                {
                    var mt = Marshal.PtrToStructure<DShow.AMMediaType>(pmt);
                    if (mt.subtype != DShow.MEDIASUBTYPE_MJPG || mt.formattype != DShow.FORMAT_VideoInfo || mt.pbFormat == 0) continue;
                    int w = Marshal.ReadInt32(mt.pbFormat, DShow.VihWidth), h = Math.Abs(Marshal.ReadInt32(mt.pbFormat, DShow.VihHeight));
                    // VIDEO_STREAM_CONFIG_CAPS.MinFrameInterval (fastest) is at offset 104
                    long minInterval = Marshal.ReadInt64(scc, 104);
                    long avg = Marshal.ReadInt64(mt.pbFormat, DShow.VihAvgTimePerFrame);
                    long best = minInterval > 0 ? minInterval : avg;
                    list.Add((w, h, best > 0 ? 10_000_000.0 / best : 0, i));
                }
                finally { DShow.Free(pmt); }
            }
        }
        finally { Marshal.FreeCoTaskMem(scc); }
        return list;
    }

    /// <summary>Open a camera by name at (about) the wanted size and frame rate, as MJPEG. Null if busy or missing.</summary>
    public static Capture? Open(string name, int wantW, int wantH, int wantFps, out string? error)
    {
        error = null;
        var cam = Find(name);
        if (cam.moniker == null) { error = "camera not found"; return null; }
        object? graphObj = null;
        try
        {
            var filter = Bind(cam.moniker);
            var graph = DShow.Create<DShow.IGraphBuilder>(DShow.CLSID_FilterGraph);
            graphObj = graph;
            Check(graph.AddFilter((DShow.IBaseFilter)filter, "camera"), "add camera");
            var builder = DShow.Create<DShow.ICaptureGraphBuilder2>(DShow.CLSID_CaptureGraphBuilder2);
            Check(builder.SetFiltergraph(graph), "graph");

            // pick the MJPEG format closest to the wanted size that can do the wanted frame rate
            var sc = StreamConfig(builder, filter);
            var caps = Caps(sc);
            if (caps.Count == 0) throw new InvalidOperationException("the camera offers no MJPEG formats");
            LastSizes = caps.OrderByDescending(c => (long)c.w * c.h).Select(c => $"{c.w}x{c.h}").Distinct().ToArray();
            var pick = caps.OrderBy(c => Math.Abs((long)c.w * c.h - (long)wantW * wantH) * 4
                                         + (c.fps >= wantFps - 0.5 ? 0 : (1L << 36) + (long)((wantFps - c.fps) * 1000)))
                           .First();
            int fps = (int)Math.Round(Math.Min(wantFps, pick.fps));
            nint scc = Marshal.AllocCoTaskMem(512);
            try
            {
                Check(sc.GetStreamCaps(pick.index, out var pmt, scc), "format");
                try
                {
                    var mt = Marshal.PtrToStructure<DShow.AMMediaType>(pmt);
                    Marshal.WriteInt64(mt.pbFormat, DShow.VihAvgTimePerFrame, 10_000_000L / fps);   // the frame rate we want
                    Check(sc.SetFormat(pmt), "set format");
                }
                finally { DShow.Free(pmt); }
            }
            finally { Marshal.FreeCoTaskMem(scc); }

            // camera -> sample grabber (keeps MJPEG as-is) -> null renderer
            var grabberObj = DShow.Create<object>(DShow.CLSID_SampleGrabber);
            var grabber = (DShow.ISampleGrabber)grabberObj;
            var want = new DShow.AMMediaType { majortype = DShow.MEDIATYPE_Video, subtype = DShow.MEDIASUBTYPE_MJPG };
            nint pwant = Marshal.AllocCoTaskMem(Marshal.SizeOf<DShow.AMMediaType>());
            try { Marshal.StructureToPtr(want, pwant, false); Check(grabber.SetMediaType(pwant), "grabber type"); }
            finally { Marshal.FreeCoTaskMem(pwant); }
            var sink = new GrabberSink();
            Check(grabber.SetBufferSamples(false), "grabber");
            Check(grabber.SetOneShot(false), "grabber");
            Check(grabber.SetCallback(sink, 1), "grabber callback");     // 1 = BufferCB
            Check(graph.AddFilter((DShow.IBaseFilter)grabberObj, "grabber"), "add grabber");
            var nullObj = DShow.Create<object>(DShow.CLSID_NullRenderer);
            Check(graph.AddFilter((DShow.IBaseFilter)nullObj, "null"), "add renderer");
            var cat = DShow.PIN_CATEGORY_CAPTURE; var type = DShow.MEDIATYPE_Video;
            Check(builder.RenderStream(ref cat, ref type, filter, (DShow.IBaseFilter)grabberObj, (DShow.IBaseFilter)nullObj), "connect");

            var control = (DShow.IMediaControl)graph;
            int run = control.Run();
            if (run < 0) throw new COMException("start", run);
            return new Capture(graph, builder, filter, grabberObj, nullObj, sink, pick.w, pick.h, fps);
        }
        catch (Exception e)
        {
            int hr = e is COMException ce ? ce.HResult : e.HResult;
            error = (uint)hr is 0x800705AA or 0x80070020 or 0xC00D3704 || e.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                ? "camera in use by another app" : "couldn't open the camera: " + e.Message;
            try { if (graphObj != null) Marshal.ReleaseComObject(graphObj); } catch { }
            return null;
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    /// <summary>
    /// The newest picture from the camera (compressed MJPEG). Returns its size in bytes,
    /// 0 when none arrived in time, -1 when the camera stopped. 'buf' grows as needed.
    /// </summary>
    public int Read(ref byte[] buf, bool wantData = true) => _sink.Take(ref buf, wantData);

    /// <summary>Total pictures delivered by the camera so far.</summary>
    public int Arrived => _sink.Arrived;

    public void Dispose()
    {
        _sink.Closed = true;
        try { _control.Stop(); } catch { }
        foreach (var o in new[] { _nullRenderer, _grabber, Filter, _builder, _graph })
            try { Marshal.ReleaseComObject(o); } catch { }
    }
}
