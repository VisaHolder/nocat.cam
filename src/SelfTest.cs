using System.Diagnostics;

namespace NocatCam;

/// <summary>
/// nocat.cam.exe --selftest out.txt : checks every part against the real camera, no windows.
/// Never moves the camera or changes its mode for longer than the test and puts it back.
/// </summary>
static class SelfTest
{
    public static int Run(string outPath)
    {
        var lines = new List<string>();
        int fails = 0;
        void Check(string what, bool ok, string extra = "")
        {
            lines.Add($"{(ok ? "PASS" : "FAIL")} {what}{(extra != "" ? "  (" + extra + ")" : "")}");
            if (!ok) fails++;
        }
        void Note(string s) => lines.Add("     " + s);
        string here = AppContext.BaseDirectory;

        try
        {
            // ---- USB control
            using (var pixy = new Pixy())
            {
                Check("camera control (USB) found", pixy.Connected);
                var mode = pixy.GetMode();
                Check("read camera mode", mode != null, $"mode {mode}");
                var pos = pixy.Position();
                Check("read camera position", pos != null, pos is { } p ? $"pan {p.pan:0.0}, tilt {p.tilt:0.0}" : "");
            }

            // ---- NV12 maths
            var src = new byte[64 * 32 * 3 / 2];
            for (int i = 0; i < 64 * 32; i++) src[i] = 200;
            for (int i = 64 * 32; i < src.Length; i++) src[i] = 100;
            var half = new Nv12(32, 16);
            Nv12.ZoomInto(src, 64, 32, 1, half);
            Check("picture shrink keeps brightness/colour", half.Data[0] == 200 && half.Data[32 * 16] == 100);
            var odd = new Nv12(48, 24);
            Nv12.ZoomInto(src, 64, 32, 1.5, odd);
            Check("zoom + non-whole resize", odd.Data[0] == 200 && odd.Data[48 * 24 + 1] == 100);

            // ---- capture
            var names = Capture.List();
            Note("cameras: " + string.Join(", ", names));
            Check("PIXY listed", names.Any(Capture.IsPixy));
            using var cap = Capture.Open("EMEET PIXY", 3840, 2160, 30, out var err);
            Check("open camera at 4K", cap != null, cap != null ? $"{cap.Width}x{cap.Height}" : err ?? "");
            if (cap != null)
            {
                using var tj = new TurboJpeg();
                var jb = new byte[1 << 20];
                Nv12? f = null;
                int eighths = TurboJpeg.Eighths(cap.Width, 1920);       // 4K camera picture decoded straight to 1080p
                bool Next() { int g = cap.Read(ref jb); if (g <= 0) return false; tj.DecodeNv12(jb.AsSpan(0, g), eighths, ref f); return true; }
                for (int i = 0; i < 10; i++) Next();
                var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew(); int n = 0;
                while (sw.Elapsed.TotalSeconds < 4) { if (Next()) n++; }
                proc.Refresh();
                double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
                double camRate = n / sw.Elapsed.TotalSeconds;
                Check("camera pictures arriving + decoding", n > 40, $"{n / sw.Elapsed.TotalSeconds:0.0} fps, {cpu:0.0}% of PC, camera {cap.Native}, decoded {f?.W}x{f?.H}");
                long sum = 0; for (int i = 0; i < f!.W * f.H; i += 997) sum += f.Data[i];
                Check("picture isn't blank", sum > 0);
                var frame = f.Data;

                // ---- picture controls
                var pc = PictureControls.Read(cap);
                Check("camera picture controls", pc.Count >= 5, string.Join(", ", pc.Keys));

                // ---- AI
                var (rgb, rw, rh) = Nv12.SmallRgb(f.Data, f.W, f.H, 1);
                using (var rec = new GestureRecognizer(Path.Combine(here, "models", "gesture_recognizer.task")))
                {
                    var t = Stopwatch.StartNew();
                    var r = rec.Recognize(rgb, rw, rh, 1);
                    Check("gesture model runs", true, $"{t.ElapsedMilliseconds} ms, hand seen: {r.hand}, sign: {r.name ?? "none"}{(r.at is { } ha ? $" at {ha.x:0.00},{ha.y:0.00}" : "")}");
                }
                using (var seg = new Segmenter(Path.Combine(here, "models", "selfie_segmenter.tflite")))
                {
                    var (sr, sw2, sh2) = Nv12.SmallRgb(f.Data, f.W, f.H, 1, 256);
                    var t = Stopwatch.StartNew();
                    var (mask, mw, mh) = seg.Mask(sr, sw2, sh2, 1);
                    int on = mask.Count(v => v > 0.5f);
                    Check("person cut-out model runs", mask.Length == mw * mh && mw > 0, $"{t.ElapsedMilliseconds} ms, {mw}x{mh}, person {100.0 * on / mask.Length:0}% of picture");
                }

                // ---- virtual camera
                using var vc = VirtualCam.TryCreate(1920, 1080, 30, out var vwhy);
                Check("virtual camera started", vc != null, vwhy ?? "");
                if (vc != null)
                {
                    var outF = new Nv12(1920, 1080);
                    var t = Stopwatch.StartNew(); int sent = 0;
                    while (t.Elapsed.TotalSeconds < 3) { if (!Next()) continue; Nv12.ZoomInto(f.Data, f.W, f.H, 1, outF); vc.Send(outF.Data); sent++; }
                    Check("sending to virtual camera keeps up with the camera", sent / t.Elapsed.TotalSeconds >= camRate * 0.9, $"{sent / t.Elapsed.TotalSeconds:0.0} fps at 1080p, camera gives {camRate:0.0}");
                    // let an outside reader (the test script) grab a frame now if it wants
                    var flag = outPath + ".vcam-ready";
                    File.WriteAllText(flag, "1");
                    var until = Stopwatch.StartNew();
                    while (until.Elapsed.TotalSeconds < 8)
                    {
                        if (!Next()) continue; Nv12.ZoomInto(f.Data, f.W, f.H, 1, outF); vc.Send(outF.Data);
                        if (until.Elapsed.TotalSeconds > 4 && until.Elapsed.TotalSeconds < 4.2)
                            Note("apps reading nocat.cam: " + string.Join(", ", VcamUsers.Readers().Select(p => { try { return Process.GetProcessById(p).ProcessName; } catch { return p.ToString(); } })));
                    }
                    File.Delete(flag);
                    // blur cost
                    var t2 = Stopwatch.StartNew(); int nb = 0;
                    var (mrgb, mw2, mh2) = Nv12.SmallRgb(f.Data, f.W, f.H, 1, 256);
                    using var seg2 = new Segmenter(Path.Combine(here, "models", "selfie_segmenter.tflite"));
                    while (t2.Elapsed.TotalSeconds < 3)
                    {
                        if (!Next()) continue;
                        Nv12.ZoomInto(f.Data, f.W, f.H, 1, outF);
                        (mrgb, mw2, mh2) = Nv12.SmallRgb(f.Data, f.W, f.H, 1, 256);
                        var (m, a, b) = seg2.Mask(mrgb, mw2, mh2, 1000 + nb);
                        Nv12.Blend(outF, Nv12.Blurred(outF, 6), m, a, b);
                        vc.Send(outF.Data); nb++;
                    }
                    Check("background blur keeps up with the camera", nb / t2.Elapsed.TotalSeconds >= camRate * 0.9, $"{nb / t2.Elapsed.TotalSeconds:0.0} fps, camera gives {camRate:0.0}");
                }
            }
        }
        catch (Exception e) { Check("no crash", false, e.ToString()); }

        lines.Add($"FAILURES: {fails}");
        File.WriteAllLines(outPath, lines);
        return fails;
    }
}
