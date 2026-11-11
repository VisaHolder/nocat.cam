using System.Diagnostics;

namespace NocatCam;

/// <summary>
/// nocat.cam.exe --speedtest out.txt : for every output size and frame rate, runs the real path
/// (camera MJPEG -> libjpeg-turbo decode -> resize -> virtual camera) for a few seconds with auto
/// exposure, and reports the frame rate reached and the work per picture.
/// </summary>
static class SpeedTest
{
    public static int Run(string outPath)
    {
        var lines = new List<string>();
        void Flush() { try { File.WriteAllLines(outPath + ".partial", lines); } catch { } }
        int fails = 0;
        (string send, int fps, string capture)[] modes =
        [
            ("3840x2160", 30, "3840x2160"),
            ("2560x1440", 30, "3840x2160"),
            ("1920x1080", 30, "3840x2160"),
            ("1920x1080", 60, "1920x1080"),
            ("1280x720", 60, "1920x1080"),
        ];
        using var tj = new TurboJpeg();
        foreach (var m in Capture.Modes("EMEET PIXY")) lines.Add("     mode " + m);

        // what's slowing the camera: auto-exposure priority (via the driver directly)
        using (var probe = Capture.Open("EMEET PIXY", 1920, 1080, 60, out var perr))
        {
            if (probe != null)
            {
                var jb = new byte[1 << 20];
                int Rate() { for (int i = 0; i < 10; i++) probe.Read(ref jb, false); int k = 0; var w = Stopwatch.StartNew(); while (w.Elapsed.TotalSeconds < 3) if (probe.Read(ref jb, false) > 0) k++; return (int)Math.Round(k / w.Elapsed.TotalSeconds); }
                var aep = KsControl.GetCameraControl(probe, KsControl.AutoExposurePriority);
                int a0 = probe.Arrived; var tw = System.Diagnostics.Stopwatch.StartNew(); int taken = Rate(); double secs = tw.Elapsed.TotalSeconds;
                lines.Add($"     1080p60 mode ({probe.Native}): taken {taken} fps, but the camera DELIVERED {(probe.Arrived - a0) / secs:0.0} fps");
                bool set0 = KsControl.SetCameraControl(probe, KsControl.AutoExposurePriority, 0);
                lines.Add($"     set priority 0: {(set0 ? "ok" : "refused")}, now reads {KsControl.GetCameraControl(probe, KsControl.AutoExposurePriority)?.ToString() ?? "?"}, camera gives {Rate()} fps");
                // one decode attempt, to see any error
                int got = probe.Read(ref jb);
                try { Nv12? f = null; tj.DecodeNv12(jb.AsSpan(0, got), 8, ref f); lines.Add($"     decode ok: {f!.W}x{f.H}"); }
                catch (Exception e) { lines.Add($"     decode error: {e.Message}; first bytes {BitConverter.ToString(jb, 0, Math.Min(16, Math.Max(0, got)))} (size {got})"); }
            }
            else lines.Add("     probe: " + perr);
        }

        foreach (var (send, fps, capture) in modes)
        {
            var (cw, ch) = Parse(capture);
            var (ow, oh) = Parse(send);
            using var cap = Capture.Open("EMEET PIXY", cw, ch, fps, out var err);
            if (cap == null) { lines.Add($"FAIL {send}@{fps}: {err}"); fails++; continue; }
            using var vc = VirtualCam.TryCreate(ow, oh, fps, out var why);
            if (vc == null) { lines.Add($"FAIL {send}@{fps}: {why}"); fails++; continue; }
            var jpeg = new byte[1 << 20];
            Nv12? frame = null;
            var outF = new Nv12(ow, oh);
            int eighths = TurboJpeg.Eighths(cap.Width, ow);
            for (int i = 0; i < 15; i++) cap.Read(ref jpeg, false);
            int n = 0, bad = 0; long work = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 5)
            {
                int got = cap.Read(ref jpeg);
                if (got <= 0) continue;
                var t = Stopwatch.GetTimestamp();
                try { tj.DecodeNv12(jpeg.AsSpan(0, got), eighths, ref frame); } catch { bad++; continue; }
                Nv12.ZoomInto(frame!.Data, frame.W, frame.H, 1, outF);
                vc.Send(outF.Data);
                work += Stopwatch.GetTimestamp() - t;
                n++;
            }
            double rate = n / sw.Elapsed.TotalSeconds, ms = work * 1000.0 / Stopwatch.Frequency / Math.Max(1, n);
            bool ok = rate >= fps * 0.93 && bad == 0;
            if (!ok) fails++;
            Flush();
            lines.Add($"{(ok ? "PASS" : "FAIL")} send {send} at {fps} fps (camera {cap.Native}, decoded at {frame?.W}x{frame?.H}): " +
                      $"{rate:0.0} fps, {ms:0.0} ms work per picture (budget {1000.0 / fps:0} ms){(bad > 0 ? $", {bad} bad pictures" : "")}");
        }
        lines.Add($"FAILURES: {fails}");
        File.WriteAllLines(outPath, lines);
        return fails;
    }

    static (int, int) Parse(string s) { var p = s.Split('x'); return (int.Parse(p[0]), int.Parse(p[1])); }
}
