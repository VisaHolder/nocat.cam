using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// "Follow only me": finds faces (MediaPipe face detector) and compares each one to the saved
/// fingerprints of your face (MediaPipe image embedder on the face crop). The camera then only
/// follows the face that matches - someone walking behind you is ignored.
/// </summary>
public sealed unsafe class FaceId : IDisposable
{
    const string Dll = "libmediapipe.dll";

    [StructLayout(LayoutKind.Sequential)]
    struct DetectorOptions
    {
        public Mp.BaseOptions base_options;
        public int running_mode;
        public float min_detection_confidence;
        public float min_suppression_threshold;
        public nint result_callback;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, bottom, right; }

    [StructLayout(LayoutKind.Sequential)]
    struct Detection
    {
        public nint categories; public uint categories_count;
        public Rect box;
        public nint keypoints; public uint keypoints_count;
    }

    [StructLayout(LayoutKind.Sequential)] struct DetectionResult { public Detection* detections; public uint count; }

    [StructLayout(LayoutKind.Sequential)]
    struct EmbedderOptions
    {
        public Mp.BaseOptions base_options;
        public int running_mode;
        public byte l2_normalize, quantize;
        public nint result_callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Embedding { public float* floats; public byte* quantized; public uint count; public int head_index; public nint head_name; }

    [StructLayout(LayoutKind.Sequential)]
    struct EmbeddingResult { public Embedding* embeddings; public uint count; public long timestamp_ms; public byte has_timestamp; }

    [DllImport(Dll)] static extern int MpFaceDetectorCreate(DetectorOptions* o, nint* h, nint* err);
    [DllImport(Dll)] static extern int MpFaceDetectorDetectImage(nint h, nint image, nint opts, DetectionResult* r, nint* err);
    [DllImport(Dll)] static extern void MpFaceDetectorCloseResult(DetectionResult* r);
    [DllImport(Dll)] static extern int MpFaceDetectorClose(nint h, nint* err);
    [DllImport(Dll)] static extern int MpImageEmbedderCreate(EmbedderOptions* o, nint* h, nint* err);
    [DllImport(Dll)] static extern int MpImageEmbedderEmbedImage(nint h, nint image, nint opts, EmbeddingResult* r, nint* err);
    [DllImport(Dll)] static extern void MpImageEmbedderCloseResult(EmbeddingResult* r);
    [DllImport(Dll)] static extern int MpImageEmbedderClose(nint h, nint* err);

    readonly nint _det, _emb;
    readonly GCHandle _detModel, _embModel;

    public FaceId(string detectorPath, string embedderPath)
    {
        var d = File.ReadAllBytes(detectorPath);
        _detModel = GCHandle.Alloc(d, GCHandleType.Pinned);
        var dopt = new DetectorOptions
        {
            base_options = new Mp.BaseOptions { model_asset_buffer = _detModel.AddrOfPinnedObject(), model_asset_buffer_count = (uint)d.Length },
            running_mode = 1, min_detection_confidence = 0.5f, min_suppression_threshold = 0.3f,
        };
        nint h, err = 0;
        try { Mp.Check(MpFaceDetectorCreate(&dopt, &h, &err), err, "face detector"); }
        catch { _detModel.Free(); throw; }
        _det = h;

        var e = File.ReadAllBytes(embedderPath);
        _embModel = GCHandle.Alloc(e, GCHandleType.Pinned);
        var eopt = new EmbedderOptions
        {
            base_options = new Mp.BaseOptions { model_asset_buffer = _embModel.AddrOfPinnedObject(), model_asset_buffer_count = (uint)e.Length },
            running_mode = 1, l2_normalize = 1, quantize = 0,
        };
        err = 0;
        try { Mp.Check(MpImageEmbedderCreate(&eopt, &h, &err), err, "face embedder"); }
        catch
        {
            nint e2 = 0; MpFaceDetectorClose(_det, &e2); if (e2 != 0) Mp.MpErrorFree(e2);
            _detModel.Free(); _embModel.Free();
            throw;
        }
        _emb = h;
    }

    public record struct Face(int X, int Y, int W, int H, float[] Print);

    /// <summary>All faces in an RGB picture, each with its fingerprint.</summary>
    public List<Face> Faces(byte[] rgb, int w, int h)
    {
        var faces = new List<Face>();
        nint img = Mp.Image(rgb, w, h), err = 0;
        try
        {
            DetectionResult r;
            Mp.Check(MpFaceDetectorDetectImage(_det, img, 0, &r, &err), err, "find faces");
            try
            {
                for (int i = 0; i < r.count; i++)
                {
                    var b = r.detections[i].box;
                    int bw = b.right - b.left, bh = b.bottom - b.top;
                    if (bw < 16 || bh < 16) continue;
                    // a bit wider than the face box (hair, chin) gives a steadier fingerprint
                    int x0 = Math.Clamp(b.left - bw / 5, 0, w - 1), y0 = Math.Clamp(b.top - bh / 5, 0, h - 1);
                    int x1 = Math.Clamp(b.right + bw / 5, x0 + 1, w), y1 = Math.Clamp(b.bottom + bh / 10, y0 + 1, h);
                    var print = Print(Crop(rgb, w, x0, y0, x1 - x0, y1 - y0), x1 - x0, y1 - y0);
                    if (print != null) faces.Add(new Face(b.left, b.top, bw, bh, print));
                }
            }
            finally { MpFaceDetectorCloseResult(&r); }
        }
        finally { Mp.MpImageFree(img); }
        return faces;
    }

    static byte[] Crop(byte[] rgb, int w, int x0, int y0, int cw, int ch)
    {
        var o = new byte[cw * ch * 3];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(rgb, ((y0 + y) * w + x0) * 3, o, y * cw * 3, cw * 3);
        return o;
    }

    float[]? Print(byte[] rgb, int w, int h)
    {
        nint img = Mp.Image(rgb, w, h), err = 0;
        try
        {
            EmbeddingResult r;
            if (MpImageEmbedderEmbedImage(_emb, img, 0, &r, &err) != 0) { if (err != 0) Mp.MpErrorFree(err); return null; }
            try
            {
                if (r.count == 0 || r.embeddings[0].floats == null) return null;
                var v = new float[r.embeddings[0].count];
                new ReadOnlySpan<float>(r.embeddings[0].floats, v.Length).CopyTo(v);
                return v;
            }
            finally { MpImageEmbedderCloseResult(&r); }
        }
        finally { Mp.MpImageFree(img); }
    }

    /// <summary>How alike two fingerprints are (cosine, 1 = identical).</summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>Best match of a face against the saved fingerprints of you.</summary>
    public static double MatchScore(float[] print, List<float[]> me) => me.Count == 0 ? 0 : me.Max(m => Similarity(print, m));

    public void Dispose()
    {
        nint err = 0;
        MpFaceDetectorClose(_det, &err); if (err != 0) Mp.MpErrorFree(err);
        err = 0;
        MpImageEmbedderClose(_emb, &err); if (err != 0) Mp.MpErrorFree(err);
        _detModel.Free(); _embModel.Free();
    }
}
