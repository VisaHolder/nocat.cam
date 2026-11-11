using System.Diagnostics;

namespace NocatCam;

/// <summary>nocat.cam.exe --cpubench picture.png out.txt : how much CPU each AI model costs at the rate the app runs it.</summary>
static class CpuBench
{
    public static int Run(string png, string outPath)
    {
        var lines = new List<string>();
        using var bmp = new Bitmap(png);
        var (rgb, w, h) = ToRgb(bmp, 640);
        var (srgb, sw, sh) = ToRgb(bmp, 256);
        string here = AppContext.BaseDirectory, m = Path.Combine(here, "models");
        var me = Process.GetCurrentProcess();

        void Measure(string name, double hz, Action step)
        {
            step();                                               // warm up
            me.Refresh(); var cpu0 = me.TotalProcessorTime; var wall = Stopwatch.StartNew();
            double next = 0; int n = 0;
            while (wall.Elapsed.TotalSeconds < 6)
            {
                double t = wall.Elapsed.TotalSeconds;
                if (t < next) { Thread.Sleep(Math.Max(1, (int)((next - t) * 1000))); continue; }
                next = t + 1 / hz; step(); n++;
            }
            me.Refresh();
            double cores = (me.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds;
            lines.Add($"{name,-28} {hz,4:0} per s: {cores * 100 / Environment.ProcessorCount,5:0.0}% of PC ({cores:0.00} cores), {me.Threads.Count} threads");
            File.WriteAllLines(outPath, lines);
        }

        {
            var frame = new Nv12(1920, 1080); new Random(1).NextBytes(frame.Data);
            var mask = new float[256 * 144]; for (int i = 0; i < mask.Length; i++) mask[i] = (i % 256) is > 80 and < 170 ? 1 : 0;
            var outF = new Nv12(1920, 1080);
            Measure("zoom only (1080p)", 30, () => Nv12.ZoomInto(frame.Data, 1920, 1080, 1.5, outF));
            Measure("zoom + blur (1080p)", 30, () => { Nv12.ZoomInto(frame.Data, 1920, 1080, 1.5, outF); Nv12.BlendBlur(outF, Nv12.Blurred(outF, 6), mask, 256, 144); });
            Measure("zoom + blur (1080p)", 5, () => { Nv12.ZoomInto(frame.Data, 1920, 1080, 1.5, outF); Nv12.BlendBlur(outF, Nv12.Blurred(outF, 6), mask, 256, 144); });
        }
        {
            // picture check: the real photo, cut out + blurred with the real models, saved next to the results
            var (frgb, fw, fh) = ToRgb(bmp, 1280);
            var nv = RgbToNv12(frgb, fw, fh);
            using var seg0 = new Segmenter(Path.Combine(m, "selfie_segmenter.tflite"));
            var (mk, mkw, mkh) = seg0.Mask(srgb, sw, sh, 1);
            Nv12.BlendBlur(nv, Nv12.Blurred(nv, 6), mk, mkw, mkh);
            var outRgb = Nv12.ToRgb(nv);
            using var ob = new Bitmap(fw, fh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var od = ob.LockBits(new Rectangle(0, 0, fw, fh), System.Drawing.Imaging.ImageLockMode.WriteOnly, ob.PixelFormat);
            var line = new byte[fw * 3];
            for (int y = 0; y < fh; y++)
            {
                for (int x = 0; x < fw; x++) { line[x * 3] = outRgb[(y * fw + x) * 3 + 2]; line[x * 3 + 1] = outRgb[(y * fw + x) * 3 + 1]; line[x * 3 + 2] = outRgb[(y * fw + x) * 3]; }
                System.Runtime.InteropServices.Marshal.Copy(line, 0, od.Scan0 + y * od.Stride, fw * 3);
            }
            ob.UnlockBits(od);
            ob.Save(Path.ChangeExtension(outPath, ".blur.png"));
        }
        long ts = 0;
        using (var rec = new GestureRecognizer(Path.Combine(m, "gesture_recognizer.task")))
            Measure("gestures", 15, () => rec.Recognize(rgb, w, h, ts += 66));
        using (var seg = new Segmenter(Path.Combine(m, "selfie_segmenter.tflite")))
            Measure("background cut-out", 15, () => seg.Mask(srgb, sw, sh, ts += 66));
        using (var id = new FaceId(Path.Combine(m, "face_detector.tflite"), Path.Combine(m, "face_embedder.tflite")))
        {
            lines.Add($"faces in picture: {id.Faces(rgb, w, h).Count}");
            Measure("faces + fingerprints", 7, () => id.Faces(rgb, w, h));
        }
        File.WriteAllLines(outPath, lines);
        return 0;
    }

    static Nv12 RgbToNv12(byte[] rgb, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { bgra[i * 4] = rgb[i * 3 + 2]; bgra[i * 4 + 1] = rgb[i * 3 + 1]; bgra[i * 4 + 2] = rgb[i * 3]; bgra[i * 4 + 3] = 255; }
        return Nv12.FromBgra(bgra, w, h);
    }

    static (byte[] rgb, int w, int h) ToRgb(Bitmap src, int width)
    {
        int w = width, h = src.Height * width / src.Width;
        using var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(b)) g.DrawImage(src, 0, 0, w, h);
        var d = b.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, b.PixelFormat);
        var o = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            var row = new byte[w * 3];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, w * 3);
            for (int x = 0; x < w; x++) { o[(y * w + x) * 3] = row[x * 3 + 2]; o[(y * w + x) * 3 + 1] = row[x * 3 + 1]; o[(y * w + x) * 3 + 2] = row[x * 3]; }
        }
        b.UnlockBits(d);
        return (o, w, h);
    }
}
