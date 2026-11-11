using System.Diagnostics;

namespace NocatCam;

/// <summary>
/// nocat.cam.exe --boosttest outdir : films 3 s from the camera at 1080p, runs the low-light boost over
/// the pictures (Auto and Strong), and saves before/after pictures plus how long it takes.
/// </summary>
static class BoostTest
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        var frames = new List<Nv12>();
        var raw = Path.Combine(dir, "frames.nv12");
        if (File.Exists(raw))
        {
            // re-use the pictures filmed last time (no need to take the camera away again)
            var all = File.ReadAllBytes(raw); int sz = 1920 * 1080 * 3 / 2;
            for (int o = 0; o + sz <= all.Length; o += sz) { var f = new Nv12(1920, 1080); Buffer.BlockCopy(all, o, f.Data, 0, sz); frames.Add(f); }
        }
        else
        {
            using var cap = Capture.Open("EMEET PIXY", 1920, 1080, 30, out var why);
            if (cap == null) { File.WriteAllText(Path.Combine(dir, "boost.txt"), "FAIL camera: " + why); return 1; }
            using var tj = new TurboJpeg();
            var jb = new byte[1 << 22];
            var w = Stopwatch.StartNew();
            while (w.Elapsed.TotalSeconds < 4)
            {
                int n = cap.Read(ref jb, true);
                if (n <= 0) { Thread.Sleep(2); continue; }
                if (w.Elapsed.TotalSeconds < 1) continue;                 // let exposure settle
                Nv12? f = null; tj.DecodeNv12(jb.AsSpan(0, n), 8, ref f);
                frames.Add(f!);
            }
            using var fs = File.Create(raw);
            foreach (var f in frames) fs.Write(f.Data);
        }
        lines.Add($"{frames.Count} pictures at {frames[0].W}x{frames[0].H}");
        Save(frames[^1], Path.Combine(dir, "before.png"));
        foreach (var mode in new[] { "auto", "strong" })
        {
            var b = new LowLight(); double ms = 0;
            Nv12? last = null;
            foreach (var f in frames)
            {
                var c = new Nv12(f.W, f.H); Buffer.BlockCopy(f.Data, 0, c.Data, 0, f.Data.Length);
                var t = Stopwatch.StartNew(); b.Apply(c, mode); ms += t.Elapsed.TotalMilliseconds;
                last = c;
            }
            Save(last!, Path.Combine(dir, $"after_{mode}.png"));
            lines.Add($"{mode}: {ms / frames.Count:0.0} ms a picture, brightening x{b.Lift:0.00}, average brightness {Mean(frames[^1]):0} -> {Mean(last!):0}");
        }
        File.WriteAllLines(Path.Combine(dir, "boost.txt"), lines);
        return 0;
    }

    static double Mean(Nv12 f) { long s = 0; int n = f.W * f.H; for (int i = 0; i < n; i += 7) s += f.Data[i]; return s / (n / 7.0); }

    static void Save(Nv12 f, string path)
    {
        var rgb = Nv12.ToRgb(f);
        using var bmp = new Bitmap(f.W, f.H, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, f.W, f.H), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var row = new byte[f.W * 3];
        for (int y = 0; y < f.H; y++)
        {
            for (int x = 0; x < f.W; x++) { int i = (y * f.W + x) * 3; row[x * 3] = rgb[i + 2]; row[x * 3 + 1] = rgb[i + 1]; row[x * 3 + 2] = rgb[i]; }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, row.Length);
        }
        bmp.UnlockBits(d);
        bmp.Save(path);
    }
}
