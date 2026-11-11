using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace NocatCam;

/// <summary>
/// Reads the camera with Windows Media Foundation. The camera sends MJPEG; Windows decodes it
/// and hands us NV12, the same format the virtual camera wants, so no conversions are needed.
/// </summary>
public sealed class Capture : IDisposable
{
    static bool _started;
    readonly IMFMediaSource _source;
    readonly IMFSourceReader _reader;
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public IMFMediaSource Source => _source;

    Capture(IMFMediaSource source, IMFSourceReader reader, int w, int h, int fps)
    { _source = source; _reader = reader; Width = w; Height = h; Fps = fps; }

    public static void Startup()
    {
        if (_started) return;
        MediaFactory.MFStartup(true).CheckError();
        _started = true;
    }

    /// <summary>Names of the video cameras Windows knows about.</summary>
    public static List<string> List()
    {
        Startup();
        var names = new List<string>();
        foreach (var a in Devices())
            using (a) names.Add(a.FriendlyName);
        return names;
    }

    static IEnumerable<IMFActivate> Devices()
    {
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        return MediaFactory.MFEnumDeviceSources(attrs);
    }

    /// <summary>Open a camera by name at (about) the wanted size, 30 fps, as NV12. Null if busy or missing.</summary>
    public static Capture? Open(string name, int wantW, int wantH, int wantFps, out string? error)
    {
        Startup();
        error = null;
        IMFActivate? dev = null;
        foreach (var a in Devices())
        {
            if (dev == null && a.FriendlyName == name) dev = a; else a.Dispose();
        }
        if (dev == null) { error = "camera not found"; return null; }

        IMFMediaSource? source = null;
        try
        {
            source = dev.ActivateObject<IMFMediaSource>();
            dev.Dispose();

            // pick the camera's own MJPEG mode closest to the wanted size at 30 fps
            using var pd = source.CreatePresentationDescriptor();
            pd.GetStreamDescriptorByIndex(0, out _, out var sd);
            using var _sd = sd;
            using var handler = sd.MediaTypeHandler;
            IMFMediaType? best = null; long bestScore = long.MaxValue;
            for (int i = 0; i < handler.MediaTypeCount; i++)
            {
                var t = handler.GetMediaTypeByIndex(i);
                var sub = t.GetGUID(MediaTypeAttributeKeys.Subtype);
                MediaFactory.MFGetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
                double fps = MaxFps(t);
                long score = Math.Abs((long)w * h - (long)wantW * wantH) + (sub == VideoFormatGuids.Mjpg ? 0 : 1L << 40)
                           + (fps >= wantFps - 1 ? (long)(fps - wantFps) : 1L << 36);
                if (score < bestScore) { best?.Dispose(); best = t; bestScore = score; } else t.Dispose();
            }
            uint useFps = (uint)wantFps;
            if (best != null)
            {
                double max = MaxFps(best);
                if (max > 0 && max + 0.5 < useFps) useFps = (uint)Math.Round(max);   // the mode can't go that fast
                MediaFactory.MFSetAttributeRatio(best, MediaTypeAttributeKeys.FrameRate, useFps, 1);
                handler.CurrentMediaType = best;
            }

            using var ra = MediaFactory.MFCreateAttributes(2);
            ra.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, ra);

            MediaFactory.MFGetAttributeSize(best!, MediaTypeAttributeKeys.FrameSize, out uint bw, out uint bh);
            best?.Dispose();
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, bw, bh);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outType);
            }
            return new Capture(source, reader, (int)bw, (int)bh, (int)useFps);
        }
        catch (Exception e)
        {
            error = (uint)e.HResult == 0xC00D3704 || e.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                ? "camera in use by another app" : "couldn't open the camera: " + e.Message;
            try { source?.Shutdown(); source?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>The fastest frame rate a camera mode allows (its range max, else its default rate).</summary>
    static double MaxFps(IMFMediaType t)
    {
        try
        {
            MediaFactory.MFGetAttributeRatio(t, MediaTypeAttributeKeys.FrameRateRangeMax, out uint n, out uint d);
            if (d != 0 && n > 0) return (double)n / d;
        }
        catch { }
        MediaFactory.MFGetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, out uint fn, out uint fd);
        return fd == 0 ? 0 : (double)fn / fd;
    }

    /// <summary>Wait for the next picture. Copies it into 'into' (W*H*3/2 bytes) when asked; false on error.</summary>
    public bool Read(byte[]? into)
    {
        using var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                                              out _, out var flags, out _);
        if ((flags & SourceReaderFlag.Error) != 0 || (flags & SourceReaderFlag.EndOfStream) != 0) return false;
        if (sample == null) return true;          // stream tick with no picture
        if (into == null) return true;
        using var buf = sample.ConvertToContiguousBuffer();
        buf.Lock(out nint ptr, out _, out int len);
        try
        {
            Marshal.Copy(ptr, into, 0, Math.Min(len, into.Length));
        }
        finally { buf.Unlock(); }
        return true;
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
        try { _source.Shutdown(); _source.Dispose(); } catch { }
    }
}
