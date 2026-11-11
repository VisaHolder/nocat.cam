using HidSharp;

namespace NocatCam;

/// <summary>
/// Talks to the EMEET PIXY over its USB control channel (HID), using the commands EMEET Studio sends.
///
///   report 0x09, group, sub, cmd, then 00 LEN 00 LEN, then payload
///   mode set: 09 01 01 00 | 00 01 00 01 | mode    (0 manual, 1 tracking, 2 privacy/sleep; 3 = awake after privacy)
///   mode get: 09 01 01 01
///   move set: 09 63 01 00 | 00 05 00 05 | axis f32 (axis 1 = pan, 2 = tilt, degrees)
///   move get: 09 63 01 01 | 00 01 00 01 | axis  -> axis, target f32, actual f32
/// </summary>
public sealed class Pixy : IDisposable
{
    public const int Vid = 0x328F, Pid = 0x00C0;
    public const int Manual = 0, Tracking = 1, Sleep = 2;

    readonly object _lock = new();
    HidStream? _stream;
    int _inLen = 32, _outLen = 32;

    HidStream? Open()
    {
        if (_stream != null) return _stream;
        // the PIXY 4K is 328F:00C0; the PIXY 2K may use another product id, so take any EMEET PIXY
        var devices = DeviceList.Local.GetHidDevices(Vid).OrderBy(d => d.ProductID == Pid ? 0 : 1);
        foreach (var d in devices)
        {
            try
            {
                if (d.GetMaxOutputReportLength() < 32) continue;   // the control interface
                if (d.ProductID != Pid)
                {
                    string pn = ""; try { pn = d.GetProductName() ?? ""; } catch { }
                    if (!pn.Contains("PIXY", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (d.TryOpen(out var s))
                {
                    s.ReadTimeout = 200; s.WriteTimeout = 500;
                    _inLen = d.GetMaxInputReportLength(); _outLen = d.GetMaxOutputReportLength();
                    _stream = s; return s;
                }
            }
            catch { }
        }
        return null;
    }

    void Drop() { try { _stream?.Dispose(); } catch { } _stream = null; }

    public bool Connected { get { lock (_lock) return Open() != null; } }

    /// <summary>Send one command; if wantReply, return the reply payload (or null).</summary>
    byte[]? Tx(byte g, byte s, byte c, byte[]? payload = null, bool wantReply = false)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var st = Open();
                if (st == null) return null;
                int n = payload?.Length ?? 0;
                var pkt = new byte[_outLen];
                pkt[0] = 0x09; pkt[1] = g; pkt[2] = s; pkt[3] = c;
                if (n > 0) { pkt[5] = (byte)n; pkt[7] = (byte)n; Array.Copy(payload!, 0, pkt, 8, n); }
                try
                {
                    Flush(st, _inLen);
                    st.Write(pkt);
                }
                catch { Drop(); continue; }       // unplugged / replugged: reopen once
                if (!wantReply) return Array.Empty<byte>();
                var buf = new byte[_inLen];
                var end = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < end)
                {
                    int r;
                    try { r = st.Read(buf); } catch (TimeoutException) { continue; } catch { Drop(); break; }
                    // replies echo the header; axis questions also echo the axis byte
                    if (r > 8 && buf[1] == g && buf[2] == s && buf[3] == c && (n == 0 || buf[8] == payload![0]))
                        return buf.AsSpan(8, Math.Min((int)buf[5], 24)).ToArray();
                }
                return null;
            }
            return null;
        }
    }

    /// <summary>Throw away old replies still queued, so we never read a stale answer.</summary>
    static void Flush(HidStream st, int len)
    {
        int old = st.ReadTimeout;
        st.ReadTimeout = 1;
        var buf = new byte[len];
        try { for (int i = 0; i < 64; i++) st.Read(buf); } catch { }
        st.ReadTimeout = old;
    }

    public bool SetMode(int mode) => Tx(0x01, 0x01, 0x00, [(byte)mode]) != null;

    public int? GetMode() => Tx(0x01, 0x01, 0x01, wantReply: true) is { Length: > 0 } r ? r[0] : null;

    /// <summary>The camera's own "auto privacy" timer in seconds (0 = never). EMEET Studio offers 10, 60, 900.</summary>
    public bool SetAutoPrivacy(int seconds) => Tx(0x02, 0x01, 0x00, BitConverter.GetBytes((uint)seconds)) != null;

    public int? GetAutoPrivacy() => Tx(0x02, 0x01, 0x01, wantReply: true) is { Length: >= 4 } r ? (int)BitConverter.ToUInt32(r, 0) : null;

    public bool MoveTo(double pan, double tilt)
    {
        bool ok = Tx(0x63, 0x01, 0x00, Axis(1, pan)) != null;
        return Tx(0x63, 0x01, 0x00, Axis(2, tilt)) != null && ok;
    }

    static byte[] Axis(byte axis, double deg)
    {
        var b = new byte[5];
        b[0] = axis;
        BitConverter.TryWriteBytes(b.AsSpan(1), (float)deg);
        return b;
    }

    /// <summary>Where the camera points now (pan, tilt in degrees), or null.</summary>
    public (double pan, double tilt)? Position()
    {
        var v = new double[2];
        for (byte axis = 1; axis <= 2; axis++)
        {
            var r = Tx(0x63, 0x01, 0x01, [axis], wantReply: true);
            if (r == null || r.Length < 9) return null;
            v[axis - 1] = BitConverter.ToSingle(r, 5);   // actual angle (1..4 is the target)
        }
        return (v[0], v[1]);
    }

    /// <summary>Move smoothly (eased in and out) instead of snapping. seconds &lt;= 0 snaps.</summary>
    public bool GlideTo(double pan, double tilt, double seconds)
    {
        var start = Position();
        if (start == null || seconds <= 0.05) return MoveTo(pan, tilt);
        int steps = Math.Max(2, (int)(seconds * 16));
        var t0 = Environment.TickCount64;
        for (int i = 1; i <= steps; i++)
        {
            double k = (double)i / steps, e = 0.5 - 0.5 * Math.Cos(Math.PI * k);
            MoveTo(start.Value.pan + (pan - start.Value.pan) * e, start.Value.tilt + (tilt - start.Value.tilt) * e);
            long wait = t0 + (long)(seconds * 1000 * k) - Environment.TickCount64;
            if (wait > 0) Thread.Sleep((int)wait);
        }
        return true;
    }

    public void Dispose() { lock (_lock) Drop(); }
}
