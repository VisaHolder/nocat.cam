using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace NocatCam;

/// <summary>
/// Owns the camera: feeds the virtual camera (zoom, background, asleep card), watches for held
/// gestures and notes when a person is in view.
///
/// Saving resources:
///  - pictures stay NV12 from camera to virtual camera (Windows decodes the camera's MJPEG);
///  - nobody showing nocat.cam: 2 pictures a second instead of 30;
///  - camera in privacy sleep: the camera is released entirely (its in-use light goes off)
///    and the asleep card is sent from memory.
/// </summary>
public sealed class Pipeline
{
    const double GestureEvery = 0.2;         // 5 checks a second still catches a 1.5 s hold
    const double IdleSendEvery = 0.5;

    readonly Func<Config> _cfg;
    readonly Func<bool> _detect;             // look for gestures right now?
    readonly Func<bool> _softAsleep;         // show the card but keep the camera on
    readonly Func<bool> _camAsleep;          // privacy sleep: let go of the camera
    readonly Action<string> _onGesture;
    readonly Action<string> _onStatus;
    readonly string _here = AppContext.BaseDirectory;
    Thread? _thread;
    volatile bool _stop;

    public string Status { get; private set; } = "starting";
    public double LastPerson { get; private set; } = Now;
    public double OpenedAt { get; private set; } = Now;
    public bool Streaming { get; private set; }
    public double Fps { get; private set; }
    public volatile bool HasReaders = true;
    public string? CaptureInfo { get; private set; }
    public Capture? Camera { get; private set; }

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double Now => Clock.Elapsed.TotalSeconds;

    public Pipeline(Func<Config> cfg, Func<bool> detect, Func<bool> softAsleep, Func<bool> camAsleep,
                    Action<string> onGesture, Action<string> onStatus)
    {
        _cfg = cfg; _detect = detect; _softAsleep = softAsleep; _camAsleep = camAsleep;
        _onGesture = onGesture; _onStatus = onStatus;
    }

    public void Start() { _thread = new Thread(Run) { IsBackground = true, Name = "pipeline" }; _thread.Start(); }
    public void Stop() { _stop = true; _thread?.Join(3000); }

    void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        _onStatus(s);
    }

    static (int w, int h) Size(string s)
    {
        var p = s.Split('x');
        return (int.Parse(p[0]), int.Parse(p[1]));
    }

    void Run()
    {
        GestureRecognizer? rec = null;
        Segmenter? seg = null;
        Capture? cap = null;
        VirtualCam? vcam = null;
        string? capKey = null, vcamKey = null;
        byte[]? frame = null;
        Nv12? outFrame = null;
        Nv12? blurCache = null;
        double nextGesture = 0, nextPresence = 0, lastSend = 0, vcamFailedUntil = 0, retry = 3;
        string? held = null; double heldSince = 0, cooldownUntil = 0; bool needRelease = false;
        int fpsN = 0; double fpsT = Now;
        var log = Log.Instance;

        void CloseCap() { cap?.Dispose(); cap = null; capKey = null; Camera = null; Streaming = false; }
        void CloseVcam() { vcam?.Dispose(); vcam = null; vcamKey = null; }

        try
        {
            while (!_stop)
            {
                var c = _cfg();
                double now = Now;
                bool wantVcam = c.virtual_cam && now >= vcamFailedUntil;
                bool detect = _detect();
                bool camAsleep = _camAsleep();
                bool soft = _softAsleep();

                // ---- virtual camera
                if (wantVcam)
                {
                    var key = c.vcam_size;
                    if (vcamKey != key)
                    {
                        CloseVcam();
                        var (vw, vh) = Size(key);
                        vcam = VirtualCam.TryCreate(vw, vh);
                        if (vcam == null)
                        {
                            vcamFailedUntil = now + 15;
                            SetStatus("virtual camera busy (is OBS's own virtual camera on?)");
                            continue;
                        }
                        vcamKey = key;
                        outFrame = new Nv12(vw, vh);
                        log.Info($"virtual camera on at {vw}x{vh}");
                    }
                }
                else CloseVcam();

                // ---- camera: only while something needs pictures from it
                // Without the virtual camera we never hold the PIXY: Discord/OBS use it directly then.
                bool needCamera = !camAsleep && vcam != null;
                if (!needCamera)
                {
                    if (cap != null) { CloseCap(); log.Info(camAsleep ? "camera released while asleep" : "camera released"); }
                    if (vcam != null && now - lastSend >= IdleSendEvery)
                    {
                        SendCard(vcam, camAsleep || soft ? "card_asleep.png" : "card_idle.png");
                        lastSend = now;
                    }
                    if (camAsleep) SetStatus("asleep");
                    else if (!c.virtual_cam) SetStatus("off (virtual camera is off)");
                    Thread.Sleep(100);
                    continue;
                }

                var ck = c.capture_size;
                if (capKey != ck)
                {
                    CloseCap();
                    var (cw, chh) = Size(ck);
                    cap = Capture.Open("EMEET PIXY", cw, chh, out var err);
                    if (cap == null)
                    {
                        SetStatus(err ?? "camera in use by another app");
                        if (vcam != null) SendCard(vcam, "card_idle.png");
                        Thread.Sleep((int)(retry * 1000));
                        retry = Math.Min(30, retry * 2);
                        continue;
                    }
                    retry = 3;
                    capKey = ck;
                    Camera = cap;
                    frame = new byte[cap.Width * cap.Height * 3 / 2];
                    CaptureInfo = $"{cap.Width}x{cap.Height}";
                    LastPerson = OpenedAt = Now;
                    Streaming = true;
                    log.Info($"camera opened at {cap.Width}x{cap.Height}");
                }

                // ---- what does this picture need to be used for?
                now = Now;
                double zoom = Math.Clamp(c.zoom, 1, 3);
                bool send = vcam != null && (HasReaders || now - lastSend >= IdleSendEvery);
                bool checkGesture = detect && now >= nextGesture;
                bool checkPresence = c.auto_sleep_away_min > 0 && now >= nextPresence;
                string bg = vcam != null && !soft ? c.bg_mode : "off";
                bool needPicture = (send && !soft) || checkGesture || checkPresence;

                if (!cap!.Read(needPicture ? frame : null)) { CloseCap(); Thread.Sleep(1000); continue; }
                if (!needPicture) continue;

                byte[]? rgb = null; int rw = 0, rh = 0;
                void Small() { if (rgb == null) (rgb, rw, rh) = Nv12.SmallRgb(frame!, cap.Width, cap.Height, zoom); }

                // ---- person mask: background effect, and once a second "is anyone there?"
                float[]? mask = null; int mw = 0, mh = 0;
                if ((send && bg != "off") || checkPresence)
                {
                    try
                    {
                        seg ??= new Segmenter(Path.Combine(_here, "models", "selfie_segmenter.tflite"));
                        Small();
                        var (sr, sw, sh) = Nv12.SmallRgb(frame!, cap.Width, cap.Height, zoom, 256);
                        (mask, mw, mh) = seg.Mask(sr, sw, sh, (long)(now * 1000));
                        if (checkPresence)
                        {
                            nextPresence = now + 1;
                            int on = 0;
                            foreach (var v in mask) if (v > 0.5f) on++;
                            if (on > mask.Length * 0.03) LastPerson = now;
                        }
                    }
                    catch (Exception e) { log.Info("person cut-out failed: " + e.Message); nextPresence = now + 30; }
                }

                // ---- virtual camera picture
                if (send)
                {
                    if (soft) SendCard(vcam!, "card_asleep.png");
                    else
                    {
                        Nv12.ZoomInto(frame!, cap.Width, cap.Height, zoom, outFrame!);
                        if (bg != "off" && mask != null)
                        {
                            Nv12? back = bg == "blur" ? Nv12.Blurred(outFrame!, c.bg_blur) : Picture(c.bg_image, outFrame!.W, outFrame.H);
                            if (back != null) Nv12.Blend(outFrame!, back, mask, mw, mh);
                        }
                        vcam!.Send(outFrame!.Data);
                    }
                    lastSend = now;
                    if (++fpsN > 0 && now - fpsT >= 2) { Fps = fpsN / (now - fpsT); fpsN = 0; fpsT = now; }
                }

                SetStatus(detect ? "watching" : "virtual camera only");

                // ---- gestures
                if (!checkGesture) { if (!detect) held = null; continue; }
                nextGesture = now + GestureEvery;
                try
                {
                    rec ??= new GestureRecognizer(Path.Combine(_here, "models", "gesture_recognizer.task"));
                    Small();
                    var (name, score, hand) = rec.Recognize(rgb!, rw, rh, (long)(now * 1000));
                    if (hand) LastPerson = now;
                    string? seen = name != null && score >= c.min_score ? name : null;
                    if (seen == null) { held = null; needRelease = false; continue; }
                    if (needRelease || now < cooldownUntil) continue;
                    if (seen != held) { held = seen; heldSince = now; continue; }
                    double hold = c.gesture_hold.TryGetValue(seen, out var h) && h > 0 ? h : c.hold_s;
                    if (now - heldSince >= hold)
                    {
                        _onGesture(seen);
                        held = null; needRelease = true;
                        cooldownUntil = now + c.cooldown_s;
                    }
                }
                catch (Exception e) { log.Info("gesture check failed: " + e.Message); nextGesture = now + 30; }
            }
        }
        catch (Exception e) { log.Info("pipeline stopped: " + e); }
        finally
        {
            CloseCap(); CloseVcam();
            rec?.Dispose(); seg?.Dispose();
        }
    }

    /// <summary>Load a picture without keeping the file locked (so it can be moved or edited).</summary>
    public static Bitmap LoadBitmap(string path)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    // ---- pictures (cards, chosen background) as NV12, cached by size
    readonly Dictionary<string, Nv12?> _pics = new();

    void SendCard(VirtualCam v, string name)
    {
        var p = Picture(Path.Combine(_here, "assets", name), v.Width, v.Height);
        if (p != null) v.Send(p.Data);
    }

    Nv12? Picture(string path, int w, int h)
    {
        if (string.IsNullOrEmpty(path)) path = Path.Combine(_here, "assets", "card_idle.png");
        var key = $"{path}|{w}x{h}";
        if (_pics.TryGetValue(key, out var hit)) return hit;
        Nv12? result = null;
        try
        {
            using var src = LoadBitmap(path);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float k = Math.Max((float)w / src.Width, (float)h / src.Height);   // cover
                float dw = src.Width * k, dh = src.Height * k;
                g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
            bmp.UnlockBits(data);
            result = Nv12.FromBgra(bytes, w, h);
        }
        catch (Exception e) { Log.Instance.Info($"couldn't load picture {path}: {e.Message}"); }
        _pics[key] = result;
        return result;
    }
}
