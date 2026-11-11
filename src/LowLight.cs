namespace NocatCam;

/// <summary>
/// Low-light boost for the picture sent to apps. In a dim room the camera's picture is dark, flat and
/// grainy; this
///   1. calms the grain by blending each pixel with the same pixel in the last picture, but only where
///      nothing moved (a pixel that changes a lot is movement and is left sharp),
///   2. brightens with a curve that lifts shadows and mid-tones much more than highlights (no blown-out
///      lamps or windows), from the picture's own darkest/brightest points, eased in over about a second
///      so it never flickers,
///   3. gives back the colour that brightening washes out.
/// All of it is whole-number lookup tables over the NV12 picture, a few milliseconds at 1080p.
/// </summary>
public sealed class LowLight
{
    byte[]? _prev;
    int _w, _h;
    double _g = 1, _black = 16, _white = 235, _sat = 1;      // eased settings
    readonly byte[] _lut = new byte[256];
    readonly byte[] _uvLut = new byte[256];
    readonly int[] _tdY = new int[511], _tdC = new int[511];
    bool _tablesMade;

    /// <summary>How much it's brightening right now (1 = not at all), for the log/settings.</summary>
    public double Lift { get; private set; } = 1;

    public void Reset() { _prev = null; }

    public void Apply(Nv12 f, string mode)
    {
        bool strong = mode == "strong";
        int w = f.W, h = f.H, ys = w * h, total = ys + ys / 2;
        var d = f.Data;
        // ---- how dark is it? (every 4th pixel is plenty)
        var hist = new int[256]; long sum = 0; int n = 0;
        for (int y = 0; y < h; y += 4)
            for (int x = 0, r = y * w; x < w; x += 4) { int v = d[r + x]; hist[v]++; sum += v; n++; }
        double mean = (double)sum / n;
        double lo = Pct(hist, n, 0.005), hi = Pct(hist, n, 0.995);

        // ---- the curve: black/white points from the picture, then a gamma that brings the middle up
        double black = Math.Clamp(lo, 16, 32), white = Math.Clamp(hi, 205, 235);   // gentle stretch: grain in bright walls stays hidden
        double mn = Math.Clamp((mean - black) / Math.Max(20, white - black), 0.02, 1);
        const double target = 0.50;                                // where the average brightness should land
        double g = mn < target ? Math.Log(target) / Math.Log(mn) : 1;
        g = Math.Clamp(g, 0.42, 1);
        if (!strong && mean > 105 && _g > 0.985)
        {
            // auto, bright room, fully eased out: leave the picture alone (no grain filter, no cost)
            _prev = null; Lift = 1;
            return;
        }
        if (!strong && mean > 105) { g = 1; black = 16; white = 235; }   // auto: easing back out
        // ease in (about a second at 30 fps) so a hand in front of the lens doesn't make it pump
        const double ease = 0.06;
        _g += (g - _g) * ease; _black += (black - _black) * ease; _white += (white - _white) * ease;
        double sat = 1 + (1 - _g) * 0.9;
        _sat += (sat - _sat) * ease;
        Lift = 1 / _g;

        for (int v = 0; v < 256; v++)
        {
            double x = Math.Clamp((v - _black) / (_white - _black), 0, 1);
            _lut[v] = (byte)Math.Clamp(16 + 219 * Math.Pow(x, _g) + 0.5, 16, 235);
            _uvLut[v] = (byte)Math.Clamp(128 + (v - 128) * _sat + 0.5, 16, 240);
        }

        // ---- grain calming: small changes are averaged with the last picture, big changes kept
        if (!_tablesMade)
        {
            _tablesMade = true;
            Ramp(_tdY, 16, 0.25);
            Ramp(_tdC, 12, 0.2);
        }

        if (_prev == null || _w != w || _h != h) { _prev = (byte[])d.Clone(); _w = w; _h = h; }
        var p = _prev; var lut = _lut; var uvLut = _uvLut; var tdY = _tdY; var tdC = _tdC;
        Nv12.Rows(h, y =>
        {
            int a = y * w, b = a + w;
            for (int i = a; i < b; i++)
            {
                int o = p[i] + tdY[d[i] - p[i] + 255];
                p[i] = (byte)o;
                d[i] = lut[o];
            }
        });
        Nv12.Rows(h / 2, y =>
        {
            int a = ys + y * w, b = a + w;
            for (int i = a; i < b; i++)
            {
                int o = p[i] + tdC[d[i] - p[i] + 255];
                p[i] = (byte)o;
                d[i] = uvLut[o];
            }
        });
    }

    /// <summary>Change table: below the threshold only part of the change is taken (noise), above it all of it (movement).</summary>
    static void Ramp(int[] t, int thr, double keep)
    {
        for (int i = 0; i < t.Length; i++)
        {
            int dlt = i - 255, ad = Math.Abs(dlt);
            double k = ad <= thr / 2 ? keep : ad >= thr ? 1 : keep + (1 - keep) * (ad - thr / 2.0) / (thr - thr / 2.0);
            t[i] = (int)Math.Round(dlt * k);
        }
    }

    static double Pct(int[] hist, int n, double q)
    {
        int want = (int)(n * q), acc = 0;
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc > want) return v; }
        return 255;
    }
}
