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
        p.Stop();
        lines.Add($"FAILURES: {fails}");
        File.WriteAllLines(outPath, lines);
        return fails;
    }
}
