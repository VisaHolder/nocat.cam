namespace NocatCam;

/// <summary>
/// nocat.cam.exe --switchtest out.txt : runs the real video pipeline and switches between every
/// size and frame rate while an outside program keeps watching nocat.cam. Writes the frame rate
/// reached at each step; the outside watcher checks the picture never stops.
/// </summary>
static class SwitchTest
{
    public static int Run(string outPath)
    {
        var cfg = new Config { virtual_cam = true, gestures_enabled = true, fixed_exposure = false };
        cfg.Set("zoom", System.Text.Json.Nodes.JsonValue.Create(1.0));
        var lines = new List<string>();
        var p = new Pipeline(() => cfg, () => true, () => false, () => false, _ => { }, s => lines.Add("     status: " + s));
        p.HasReaders = true;
        p.Start();
        (string size, int fps)[] steps =
        [
            ("1920x1080", 30), ("1920x1080", 60), ("2560x1440", 30), ("3840x2160", 30),
            ("1280x720", 60), ("1920x1080", 30), ("1920x1080", 60), ("1920x1080", 30),
        ];
        int fails = 0;
        File.WriteAllText(outPath + ".steps", "");
        foreach (var (size, fps) in steps)
        {
            cfg.vcam_size = size; cfg.fps = fps;
            File.AppendAllText(outPath + ".steps", $"{DateTime.Now:HH:mm:ss.fff} {size}@{fps}\n");
            Thread.Sleep(7000);
            bool ok = p.Fps > 15 && p.Streaming;
            if (!ok) fails++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} {size} at {fps} fps: sending {p.Fps:0.0} fps, camera {p.CaptureInfo}");
        }
        // photo: switches to the biggest mode for a moment, saves, comes back
        string? photo = null; var got = new ManualResetEventSlim();
        p.OnSnapshot += path => { photo = path; got.Set(); };
        cfg.vcam_size = "1920x1080"; cfg.fps = 60; Thread.Sleep(3000);
        p.SnapshotRequested = true;
        bool done = got.Wait(15000);
        string dims = "";
        if (photo != null) { using var im = Image.FromFile(photo); dims = $"{im.Width}x{im.Height}"; }
        bool pOk = done && photo != null;
        if (!pOk) fails++;
        lines.Add($"{(pOk ? "PASS" : "FAIL")} photo: {photo ?? "none"} {dims}");
        Thread.Sleep(3000);
        bool back = p.Fps > 15 && p.CaptureInfo == "1920x1080";
        if (!back) fails++;
        lines.Add($"{(back ? "PASS" : "FAIL")} back to normal after the photo: sending {p.Fps:0.0} fps, camera {p.CaptureInfo}");
        // be right back + low light: keeps sending
        p.Brb = true; Thread.Sleep(3000);
        bool brb = p.Fps > 15; if (!brb) fails++;
        lines.Add($"{(brb ? "PASS" : "FAIL")} be right back card: sending {p.Fps:0.0} fps");
        p.Brb = false; cfg.low_light = "strong"; Thread.Sleep(3000);
        bool ll = p.Fps > 15; if (!ll) fails++;
        lines.Add($"{(ll ? "PASS" : "FAIL")} low-light boost on: sending {p.Fps:0.0} fps");
        p.Stop();
        lines.Add($"FAILURES: {fails}");
        File.WriteAllLines(outPath, lines);
        return fails;
    }
}
