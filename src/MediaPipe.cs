using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Google MediaPipe Tasks through its C API (libmediapipe.dll, Apache 2.0) - the same engine and
/// models the original Python version used: gesture recognizer and selfie (person) segmenter.
/// Every status call takes a trailing char** error message and returns 0 for OK.
/// </summary>
static unsafe class Mp
{
    const string Dll = "libmediapipe.dll";
    public const int Video = 2;            // running mode
    public const int Srgb = 1;             // image format

    [StructLayout(LayoutKind.Sequential)]
    public struct BaseOptions
    {
        public nint model_asset_buffer;
        public uint model_asset_buffer_count;
        public nint model_asset_path;
        public int file_descriptor;
        public int @delegate;
        public int host_environment;
        public int host_system;
        public nint host_version;
        public nint ca_bundle_path;
        public nint app_id;
        public nint app_version;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClassifierOptions
    {
        public nint display_names_locale;
        public int max_results;
        public float score_threshold;
        public nint category_allowlist;
        public uint category_allowlist_count;
        public nint category_denylist;
        public uint category_denylist_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GestureOptions
    {
        public BaseOptions base_options;
        public int running_mode;
        public int num_hands;
        public float min_hand_detection_confidence;
        public float min_hand_presence_confidence;
        public float min_tracking_confidence;
        public ClassifierOptions canned;
        public ClassifierOptions custom;
        public nint result_callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Category { public int index; public float score; public nint category_name; public nint display_name; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Categories { public Category* categories; public uint count; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Landmarks { public nint landmarks; public uint count; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GestureResult
    {
        public Categories* gestures; public uint gestures_count;
        public Categories* handedness; public uint handedness_count;
        public Landmarks* hand_landmarks; public uint hand_landmarks_count;
        public Landmarks* hand_world_landmarks; public uint hand_world_landmarks_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SegmenterOptions
    {
        public BaseOptions base_options;
        public int running_mode;
        public nint display_names_locale;
        public byte output_confidence_masks;
        public byte output_category_mask;
        public nint result_callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SegmenterResult
    {
        public nint* confidence_masks; public uint confidence_masks_count;
        public nint category_mask; public uint has_category_mask;
        public float* quality_scores; public uint quality_scores_count;
    }

    [DllImport(Dll)] public static extern int MpGestureRecognizerCreate(GestureOptions* o, nint* handle, nint* err);
    [DllImport(Dll)] public static extern int MpGestureRecognizerRecognizeForVideo(nint h, nint image, nint procOptions, long tsMs, GestureResult* r, nint* err);
    [DllImport(Dll)] public static extern void MpGestureRecognizerCloseResult(GestureResult* r);
    [DllImport(Dll)] public static extern int MpGestureRecognizerClose(nint h, nint* err);

    [DllImport(Dll)] public static extern int MpImageSegmenterCreate(SegmenterOptions* o, nint* handle, nint* err);
    [DllImport(Dll)] public static extern int MpImageSegmenterSegmentForVideo(nint h, nint image, nint procOptions, long tsMs, SegmenterResult* r, nint* err);
    [DllImport(Dll)] public static extern void MpImageSegmenterCloseResult(SegmenterResult* r);
    [DllImport(Dll)] public static extern int MpImageSegmenterClose(nint h, nint* err);

    [DllImport(Dll)] public static extern int MpImageCreateFromUint8Data(int format, int w, int h, byte* data, int size, nint* image, nint* err);
    [DllImport(Dll)] public static extern int MpImageDataFloat32(nint image, float** data, nint* err);
    [DllImport(Dll)] public static extern int MpImageGetWidth(nint image);
    [DllImport(Dll)] public static extern int MpImageGetHeight(nint image);
    [DllImport(Dll)] public static extern void MpImageFree(nint image);
    [DllImport(Dll)] public static extern void MpErrorFree(nint err);

    public static void Check(int status, nint err, string what)
    {
        if (status == 0) return;
        string msg = err != 0 ? Marshal.PtrToStringUTF8(err) ?? "" : "";
        if (err != 0) MpErrorFree(err);
        throw new InvalidOperationException($"{what} failed ({status}): {msg}");
    }

    public static nint Image(byte[] rgb, int w, int h)
    {
        nint img, err = 0;
        fixed (byte* p = rgb) Check(MpImageCreateFromUint8Data(Srgb, w, h, p, rgb.Length, &img, &err), err, "image");
        return img;
    }
}

/// <summary>Hand gesture recogniser (what sign is a hand making).</summary>
public sealed unsafe class GestureRecognizer : IDisposable
{
    readonly nint _h;
    readonly GCHandle _model;
    long _lastTs = -1;

    public GestureRecognizer(string modelPath)
    {
        var bytes = File.ReadAllBytes(modelPath);
        _model = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var o = new Mp.GestureOptions
        {
            base_options = new Mp.BaseOptions { model_asset_buffer = _model.AddrOfPinnedObject(), model_asset_buffer_count = (uint)bytes.Length },
            running_mode = Mp.Video, num_hands = 2,
            min_hand_detection_confidence = 0.5f, min_hand_presence_confidence = 0.5f, min_tracking_confidence = 0.5f,
            canned = new Mp.ClassifierOptions { max_results = -1, score_threshold = 0 },
            custom = new Mp.ClassifierOptions { max_results = -1, score_threshold = 0 },
        };
        nint h, err = 0;
        Mp.Check(Mp.MpGestureRecognizerCreate(&o, &h, &err), err, "gesture recogniser");
        _h = h;
    }

    // MpNormalizedLandmark: x, y, z floats, then has_visibility/visibility/has_presence/presence, then name (40 bytes)
    const int LandmarkSize = 40;

    /// <summary>Returns (best gesture name or null, its score, whether any hand was seen, where that hand is (0..1)).</summary>
    public (string? name, float score, bool hand, (double x, double y)? at) Recognize(byte[] rgb, int w, int h, long tsMs)
    {
        tsMs = Math.Max(tsMs, _lastTs + 1); _lastTs = tsMs;
        nint img = Mp.Image(rgb, w, h), err = 0;
        try
        {
            Mp.GestureResult r;
            Mp.Check(Mp.MpGestureRecognizerRecognizeForVideo(_h, img, 0, tsMs, &r, &err), err, "recognise");
            try
            {
                bool hand = r.hand_landmarks_count > 0;
                string? best = null; float bestScore = 0; (double, double)? at = null;
                for (int i = 0; i < r.gestures_count; i++)
                {
                    var cats = r.gestures[i];
                    if (cats.count == 0) continue;
                    var c = cats.categories[0];
                    var name = Marshal.PtrToStringUTF8(c.category_name);
                    if (name != null && Gestures.Names.ContainsKey(name) && c.score > bestScore)
                    {
                        best = name; bestScore = c.score;
                        at = i < r.hand_landmarks_count ? Centre(r.hand_landmarks[i]) : null;
                    }
                }
                return (best, bestScore, hand, at);
            }
            finally { Mp.MpGestureRecognizerCloseResult(&r); }
        }
        finally { Mp.MpImageFree(img); }
    }

    /// <summary>Middle of a hand: average of its points.</summary>
    static (double, double)? Centre(Mp.Landmarks l)
    {
        if (l.count == 0 || l.landmarks == 0) return null;
        double sx = 0, sy = 0;
        for (int k = 0; k < l.count; k++)
        {
            var p = (float*)((byte*)l.landmarks + k * LandmarkSize);
            sx += p[0]; sy += p[1];
        }
        return (sx / l.count, sy / l.count);
    }

    public void Dispose()
    {
        nint err = 0;
        Mp.MpGestureRecognizerClose(_h, &err);
        if (err != 0) Mp.MpErrorFree(err);
        _model.Free();
    }
}

/// <summary>Person cut-out: a 0..1 mask of where a person is.</summary>
public sealed unsafe class Segmenter : IDisposable
{
    readonly nint _h;
    readonly GCHandle _model;
    long _lastTs = -1;

    public Segmenter(string modelPath)
    {
        var bytes = File.ReadAllBytes(modelPath);
        _model = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var empty = Marshal.StringToCoTaskMemUTF8("");
        var o = new Mp.SegmenterOptions
        {
            base_options = new Mp.BaseOptions { model_asset_buffer = _model.AddrOfPinnedObject(), model_asset_buffer_count = (uint)bytes.Length },
            running_mode = Mp.Video, display_names_locale = empty, output_confidence_masks = 1, output_category_mask = 0,
        };
        nint h, err = 0;
        try { Mp.Check(Mp.MpImageSegmenterCreate(&o, &h, &err), err, "segmenter"); }
        finally { Marshal.FreeCoTaskMem(empty); }
        _h = h;
    }

    public (float[] mask, int w, int h) Mask(byte[] rgb, int w, int h, long tsMs)
    {
        tsMs = Math.Max(tsMs, _lastTs + 1); _lastTs = tsMs;
        nint img = Mp.Image(rgb, w, h), err = 0;
        try
        {
            Mp.SegmenterResult r;
            Mp.Check(Mp.MpImageSegmenterSegmentForVideo(_h, img, 0, tsMs, &r, &err), err, "segment");
            try
            {
                nint m = r.confidence_masks[0];
                int mw = Mp.MpImageGetWidth(m), mh = Mp.MpImageGetHeight(m);
                float* data; nint e2 = 0;
                Mp.Check(Mp.MpImageDataFloat32(m, &data, &e2), e2, "mask data");
                var mask = new float[mw * mh];
                new ReadOnlySpan<float>(data, mask.Length).CopyTo(mask);
                return (mask, mw, mh);
            }
            finally { Mp.MpImageSegmenterCloseResult(&r); }
        }
        finally { Mp.MpImageFree(img); }
    }

    public void Dispose()
    {
        nint err = 0;
        Mp.MpImageSegmenterClose(_h, &err);
        if (err != 0) Mp.MpErrorFree(err);
        _model.Free();
    }
}
