using System.Diagnostics;

namespace NocatCam;

/// <summary>
/// nocat.cam.exe --speedtest out.txt : for every output size and frame rate, runs the real
/// camera -> resize -> virtual camera path for a few seconds and reports the frame rate reached
/// and how long each picture takes. Also checks which exposure settings let the camera keep up.
/// Exposure is put back how it was afterwards.
/// </summary>
static class SpeedTest
{
    public static int Run(string outPath)
    {
        var lines = new List<string>();
        int fails = 0;
        Capture? probe = Capture.Open("EMEET PIXY", 1920, 1080, 30, out var err);
        if (probe == null) { File.WriteAllLines(outPath, ["FAIL camera: " + err]); return 1; }
        var before = PictureControls.Read(probe);
        lines.Add("picture controls now: " + string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value.value}{(kv.Value.auto ? " auto" : "")}")));
        probe.Dispose();

        // 1) what keeps the frame rate up: auto exposure, with "auto-exposure priority" on and off
        using (var cap = Capture.Open("EMEET PIXY", 1920, 1080, 60, out err))
        {
            if (cap != null)
            {
                lines.Add($"     camera mode opened: {cap.Width}x{cap.Height} @ {cap.Fps}");
                var aep = PictureControls.RawCameraControl(cap, PictureControls.AutoExposurePriority);
                lines.Add("     auto-exposure priority control: " + (aep == null ? "not available" : $"range {aep.Value.min}..{aep.Value.max}, now {aep.Value.value}"));
                var frame = new byte[cap.Width * cap.Height * 3 / 2];
                foreach (var (label, val, auto, prio) in new (string, int, bool, int)[]
                         { ("auto, priority 1", -5, true, 1), ("auto, priority 0", -5, true, 0), ("-5 fixed", -5, false, 0) })
                {
                    if (aep != null) PictureControls.SetRawCameraControl(cap, PictureControls.AutoExposurePriority, prio);
                    PictureControls.Set(cap, "Exposure", val, auto);
                    for (int i = 0; i < 20; i++) cap.Read(frame);
                    var rb = PictureControls.Read(cap)["Exposure"];
                    int n = 0; var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 3) { cap.Read(frame); n++; }
                    long sum = 0; for (int i = 0; i < cap.Width * cap.Height; i += 101) sum += frame[i];
                    double bright = sum / (double)(cap.Width * cap.Height / 101);
                    lines.Add($"     exposure {label}: camera gives {n / sw.Elapsed.TotalSeconds:0.0} fps, brightness {bright:0} (read back {rb.value}{(rb.auto ? " auto" : "")})");
                }
            }
        }

        (string send, int fps, string capture)[] modes =
        [
            ("3840x2160", 30, "3840x2160"),
            ("2560x1440", 30, "3840x2160"),
            ("1920x1080", 30, "3840x2160"),
            ("1920x1080", 60, "1920x1080"),
            ("1280x720", 60, "1920x1080"),
        ];
        foreach (var (send, fps, capture) in modes)
        {
            var (cw, ch) = Parse(capture);
            var (ow, oh) = Parse(send);
            using var cap = Capture.Open("EMEET PIXY", cw, ch, fps, out err);
            if (cap == null) { lines.Add($"FAIL {send}@{fps}: {err}"); fails++; continue; }
            PictureControls.Set(cap, "Exposure", -5, auto: true);
            PictureControls.SetRawCameraControl(cap, PictureControls.AutoExposurePriority, 0);
            using var vc = VirtualCam.TryCreate(ow, oh, fps);
            if (vc == null) { lines.Add($"FAIL {send}@{fps}: virtual camera busy"); fails++; continue; }
            var frame = new byte[cap.Width * cap.Height * 3 / 2];
            var outF = new Nv12(ow, oh);
            for (int i = 0; i < 20; i++) cap.Read(frame);
            int n = 0; long work = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 5)
            {
                cap.Read(frame);
                var t = Stopwatch.GetTimestamp();
                Nv12.ZoomInto(frame, cap.Width, cap.Height, 1, outF);
                vc.Send(outF.Data);
                work += Stopwatch.GetTimestamp() - t;
                n++;
            }
            double got = n / sw.Elapsed.TotalSeconds, ms = work * 1000.0 / Stopwatch.Frequency / n;
            bool ok = got >= fps * 0.93;
            if (!ok) fails++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} send {send} at {fps} fps (camera {cap.Width}x{cap.Height}@{cap.Fps}): {got:0.0} fps, {ms:0.0} ms work per picture (budget {1000.0 / fps:0} ms)");
        }

        using (var cap = Capture.Open("EMEET PIXY", 1920, 1080, 30, out _))
            if (cap != null && before.TryGetValue("Exposure", out var e))
            {
                PictureControls.Set(cap, "Exposure", e.value, e.auto);
                PictureControls.SetRawCameraControl(cap, PictureControls.AutoExposurePriority, 1);
            }

        lines.Add($"FAILURES: {fails}");
        File.WriteAllLines(outPath, lines);
        return fails;
    }

    static (int, int) Parse(string s) { var p = s.Split('x'); return (int.Parse(p[0]), int.Parse(p[1])); }
}
