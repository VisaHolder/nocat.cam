using System.Diagnostics;

namespace NocatCam;

/// <summary>nocat.cam.exe --facetest out.txt : finds faces on the real camera for 10 s and reports how
/// steady the fingerprint of the main face is (same person should score high frame to frame).</summary>
static class FaceTest
{
    public static int Run(string outPath)
    {
        var lines = new List<string>();
        // blur-everyone-but-me: two people in a mask, keep the one under the face
        {
            const int mw = 64, mh = 36; var m = new float[mw * mh];
            for (int y = 5; y < 36; y++) { for (int x = 5; x < 20; x++) m[y * mw + x] = 1; for (int x = 40; x < 60; x++) m[y * mw + x] = 0.9f; }
            var ka = Pipeline.KeepOneForTest(m, mw, mh, (0.2, 0.3));      // face over the left person
            var kb = Pipeline.KeepOneForTest(m, mw, mh, null);            // no face: biggest person
            bool ok = ka[10 * mw + 10] == 1 && ka[10 * mw + 50] == 0 && kb[10 * mw + 50] > 0 && kb[10 * mw + 10] == 0;
            lines.Add($"{(ok ? "PASS" : "FAIL")} keep only me in the blur mask");
        }
        string here = AppContext.BaseDirectory;
        // a face-down camera can't see a face: lift it for the test, and put it back down afterwards
        using var px = new Pixy();
        bool wasDown = px.GetMode() == Pixy.Sleep;
        if (wasDown) { px.SetMode(Pixy.Manual); Thread.Sleep(3000); lines.Add("     camera was face-down: lifted for the test"); }
        try { return RunCamera(lines, here, outPath); }
        finally { if (wasDown) px.SetMode(Pixy.Sleep); }
    }

    static int RunCamera(List<string> lines, string here, string outPath)
    {
        using var cap = Capture.Open("EMEET PIXY", 1920, 1080, 30, out var why);
        if (cap == null) { File.WriteAllText(outPath, "FAIL camera: " + why); return 1; }
        using var tj = new TurboJpeg();
        using var id = new FaceId(Path.Combine(here, "models", "face_detector.tflite"), Path.Combine(here, "models", "face_embedder.tflite"));
        var jb = new byte[1 << 21]; Nv12? f = null;
        float[]? first = null; var sims = new List<double>(); int frames = 0, withFace = 0; double ms = 0;
        var w = Stopwatch.StartNew();
        while (w.Elapsed.TotalSeconds < 10)
        {
            int n = cap.Read(ref jb, true); if (n <= 0) { Thread.Sleep(5); continue; }
            tj.DecodeNv12(jb.AsSpan(0, n), 8, ref f);
            var (rgb, rw, rh) = Nv12.SmallRgb(f!.Data, f.W, f.H, 1, 640);
            var t = Stopwatch.StartNew();
            var faces = id.Faces(rgb, rw, rh);
            ms += t.Elapsed.TotalMilliseconds; frames++;
            if (faces.Count > 0)
            {
                withFace++;
                var big = faces.OrderByDescending(x => x.W * x.H).First();
                if (first == null) { first = big.Print; lines.Add($"     first face {big.W}x{big.H} at {big.X},{big.Y}, print {big.Print.Length} numbers, {faces.Count} faces"); }
                else sims.Add(FaceId.Similarity(first, big.Print));
            }
            Thread.Sleep(150);
        }
        lines.Insert(0, $"{(withFace > frames / 2 ? "PASS" : "FAIL")} face found in {withFace}/{frames} pictures, {ms / Math.Max(1, frames):0.0} ms each");
        if (sims.Count > 0) lines.Add($"     same face score: min {sims.Min():0.000} avg {sims.Average():0.000} max {sims.Max():0.000}");
        File.WriteAllLines(outPath, lines);
        return withFace > frames / 2 ? 0 : 1;
    }
}
