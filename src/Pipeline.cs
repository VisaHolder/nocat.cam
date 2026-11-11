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
    readonly ManualResetEvent _stopEvent = new(false);
    VirtualCam? _liveVcam;                           // so Stop() can tell readers "stopping" even if the loop is stuck

    public string Status { get; private set; } = "starting";
    public double LastPerson { get; internal set; } = Now;
    public double OpenedAt { get; private set; } = Now;
    public bool Streaming { get; private set; }
    public double Fps { get; private set; }
    public volatile bool HasReaders = true;
    public string? CaptureInfo { get; private set; }
    public Capture? Camera { get; private set; }
    /// <summary>Raised when the camera is (re)opened - the app re-applies exposure for the frame rate.</summary>
    public event Action? CameraOpened;

    /// <summary>Set by the app while whole-body tracking is on: measure where the person is 5 times a second.</summary>
    public volatile bool WantBody;
    /// <summary>Follow only the face that matches the saved fingerprints ("only me").</summary>
    public volatile bool FaceLock;
    /// <summary>Set to ask the AI to save the face it sees now as "me"; the result arrives via OnRemembered.</summary>
    public volatile bool RememberRequested;
    public event Action<float[]?>? OnRemembered;
    /// <summary>Someone else's face, seen at the same time as yours (so it's surely not you).</summary>
    public event Action<float[]>? OnLearnedOther;
    /// <summary>Last face-lock result for the settings page: true = following you, false = can't see you.</summary>
    public volatile bool FaceFound;
    /// <summary>Latest person box in the picture, as fractions 0..1: centre x, top y, bottom y, share of picture.</summary>
    public BodyInfo Body { get => _body; internal set => _body = value; }
    volatile BodyInfo _body = new(0.5, 0, 1, 0, -10);

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double Now => Clock.Elapsed.TotalSeconds;

    public Pipeline(Func<Config> cfg, Func<bool> detect, Func<bool> softAsleep, Func<bool> camAsleep,
                    Action<string> onGesture, Action<string> onStatus)
    {
        _cfg = cfg; _detect = detect; _softAsleep = softAsleep; _camAsleep = camAsleep;
        _onGesture = onGesture; _onStatus = onStatus;
    }

    Ai _ai = null!;

    public void Start()
    {
        _ai = new Ai(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "pipeline", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }
    public void Stop()
    {
        _stop = true;
        _stopEvent.Set();
        if (_thread != null && !_thread.Join(3000))
            _liveVcam?.MarkStopping();               // loop stuck (camera hung): don't leave Discord on a frozen frame
    }

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
        Capture? cap = null;
        VirtualCam? vcam = null;
        string? capKey = null, vcamKey = null;
        byte[] jpeg = new byte[1 << 20];            // the camera's compressed picture
        Nv12? frame = null;                          // decoded (already shrunk to what's needed)
        TurboJpeg? tj = null;
        int badPictures = 0;
        Nv12? outFrame = null;
        double nextGesture = 0, nextMask = 0, nextPresence = 0, lastSend = 0, vcamFailedUntil = 0, retry = 3, perfLogAt = 0;
        int fpsN = 0; double fpsT = Now;
        long readTicks = 0, workTicks = 0;
        var log = Log.Instance;

        void CloseCap() { cap?.Dispose(); cap = null; capKey = null; Camera = null; Streaming = false; }
        void CloseVcam() { vcam?.Dispose(); vcam = null; vcamKey = null; _liveVcam = null; }

        try
        {
            while (!_stop)
            {
              try
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
                    var key = c.vcam_size + "@" + c.fps;
                    if (vcamKey != key)
                    {
                        var (vw, vh) = Size(c.vcam_size);
                        // change size/fps in place: apps already showing nocat.cam keep showing it
                        if (vcam == null || !vcam.Configure(vw, vh, c.fps))
                        {
                            CloseVcam();
                            vcam = VirtualCam.TryCreate(vw, vh, c.fps, out var why);
                            if (vcam == null)
                            {
                                vcamFailedUntil = now + 5;
                                SetStatus(why ?? "virtual camera busy");
                                _stopEvent.WaitOne(500);
                                continue;
                            }
                        }
                        vcamKey = key;
                        _liveVcam = vcam;
                        if (outFrame == null || outFrame.W != vw || outFrame.H != vh) outFrame = new Nv12(vw, vh);
                        log.Info($"virtual camera on at {vw}x{vh} {c.fps} fps");
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
                    _stopEvent.WaitOne(100);
                    continue;
                }

                // Up to 1080p out: read the camera's own 1080p mode - it runs up to 60 fps and keeps 30 in dim
                // light (its 4K mode drops to 20 in dim light, which looked choppy). 1440p/4K out: the 4K mode.
                bool small = c.vcam_size is "1920x1080" or "1280x720";
                var ck = small ? "1920x1080@" + (c.fps == 60 ? 60 : 30) : c.capture_size + "@30";
                if (capKey != ck)
                {
                    CloseCap();
                    var (cw, chh) = Size(ck.Split('@')[0]);
                    cap = Capture.Open("EMEET PIXY", cw, chh, c.fps, out var err);
                    if (cap == null)
                    {
                        SetStatus(err ?? "camera in use by another app");
                        if (vcam != null) SendCard(vcam, "card_idle.png");
                        _stopEvent.WaitOne((int)(retry * 1000));
                        retry = Math.Min(30, retry * 2);
                        continue;
                    }
                    retry = 3;
                    capKey = ck;
                    Camera = cap;
                    tj ??= new TurboJpeg();
                    CaptureInfo = $"{cap.Width}x{cap.Height}";
                    LastPerson = OpenedAt = Now;
                    Streaming = true;
                    log.Info($"camera opened at {cap.Width}x{cap.Height} {cap.Fps} fps (camera mode {cap.Native})");
                    CameraOpened?.Invoke();
                }

                // ---- what does this picture need to be used for?
                now = Now;
                double zoom = Math.Clamp(c.zoom, 1, 3);
                bool send = vcam != null && (HasReaders || now - lastSend >= IdleSendEvery);
                string bg = vcam != null && !soft ? c.bg_mode : "off";
                bool gestureDue = detect && now >= nextGesture;
                bool presenceDue = (c.auto_sleep_away_min > 0 || WantBody || RememberRequested) && now >= nextPresence;
                bool maskDue = send && bg != "off" && now >= nextMask;    // blur/picture: a fresh outline 15 times a second is plenty
                bool handAi = !_ai.Busy && (gestureDue || presenceDue || maskDue);
                bool needPicture = (send && !soft) || handAi;

                var t0 = Stopwatch.GetTimestamp();
                int got = cap!.Read(ref jpeg, needPicture);
                readTicks += Stopwatch.GetTimestamp() - t0;
                if (got < 0) { CloseCap(); log.Info("camera stopped sending - reopening"); _stopEvent.WaitOne(1000); continue; }
                if (got == 0 || !needPicture) continue;
                t0 = Stopwatch.GetTimestamp();

                // decode, shrinking on the way: only as big as the picture we send (or the AI needs)
                int needW = (int)Math.Ceiling(((send && !soft) ? outFrame!.W : 640) * zoom);
                try { tj!.DecodeNv12(jpeg.AsSpan(0, got), TurboJpeg.Eighths(cap.Width, needW), ref frame); badPictures = 0; }
                catch (Exception e)
                {
                    if (++badPictures == 30) log.Info("camera pictures won't decode: " + e.Message);
                    continue;                        // skip a damaged picture
                }

                // ---- AI runs on its own thread with a copy of this picture, so video never waits for it
                if (handAi)
                {
                    if (gestureDue) nextGesture = now + GestureEvery;
                    if (maskDue) nextMask = now + 1.0 / 15;
                    if (presenceDue) nextPresence = now + (WantBody || RememberRequested ? 0.2 : 1);
                    _ai.Submit(frame!.Data, frame.W, frame.H, zoom, gestureDue, presenceDue || maskDue, presenceDue, c);
                }

                // ---- virtual camera picture
                if (send)
                {
                    if (soft) SendCard(vcam!, "card_asleep.png");
                    else
                    {
                        Nv12.ZoomInto(frame!.Data, frame.W, frame.H, zoom, outFrame!);
                        var m = _ai.Mask;
                        if (bg != "off" && m.mask != null)
                        {
                            if (bg == "blur") Nv12.BlendBlur(outFrame!, Nv12.Blurred(outFrame!, c.bg_blur), m.mask, m.w, m.h);
                            else if (Picture(c.bg_image, outFrame!.W, outFrame.H) is { } back) Nv12.Blend(outFrame!, back, m.mask, m.w, m.h);
                        }
                        vcam!.Send(outFrame!.Data);
                    }
                    lastSend = now;
                    fpsN++;
                }
                workTicks += Stopwatch.GetTimestamp() - t0;
                if (now - fpsT >= 2)
                {
                    Fps = fpsN / (now - fpsT);
                    if (now - perfLogAt > 60 && HasReaders)
                    {
                        perfLogAt = now;
                        double perRead = readTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsN);
                        double perWork = workTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, fpsN);
                        log.Info($"video: {Fps:0.0} fps sent, per picture {perRead:0.0} ms waiting for the camera, {perWork:0.0} ms work");
                    }
                    fpsN = 0; fpsT = now; readTicks = workTicks = 0;
                }

                SetStatus(detect ? "watching" : "virtual camera only");
                if (!detect) _ai.ClearGesture();
              }
              catch (Exception e)
              {
                  // never let one error stop the video: log it, reopen the camera, carry on
                  log.Info("video error, recovering: " + e.Message);
                  CloseCap();
                  _stopEvent.WaitOne(1000);
              }
            }
        }
        catch (Exception e) { log.Info("pipeline stopped: " + e); }
        finally
        {
            CloseCap(); CloseVcam();
            _ai.Dispose();
            tj?.Dispose();
        }
    }

    /// <summary>
    /// The AI side (gestures, person outline) on its own thread. It always works on the newest picture
    /// it was handed and skips any that arrive while it's busy, so it can never slow the video down.
    /// </summary>
    sealed class Ai : IDisposable
    {
        readonly Pipeline _p;
        readonly Thread _t;
        readonly AutoResetEvent _go = new(false);
        byte[] _buf = [];
        int _w, _h; double _zoom; bool _gesture, _mask, _presence; Config? _cfg;
        FaceId? _faces;
        (double cx, double cy) _lastFace = (0.5, 0.35);
        volatile bool _busy, _stop;
        GestureRecognizer? _rec;
        Segmenter? _seg;
        string? _held; double _heldSince, _cooldownUntil; bool _needRelease;
        public bool Busy => _busy;
        public (float[]? mask, int w, int h) Mask { get; private set; }

        public Ai(Pipeline p)
        {
            _p = p;
            _t = new Thread(Run) { IsBackground = true, Name = "ai", Priority = ThreadPriority.BelowNormal };
            _t.Start();
        }

        public void Submit(byte[] frame, int w, int h, double zoom, bool gesture, bool mask, bool presence, Config cfg)
        {
            if (_busy) return;
            if (_buf.Length != frame.Length) _buf = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _buf, 0, frame.Length);
            (_w, _h, _zoom, _gesture, _mask, _presence, _cfg) = (w, h, zoom, gesture, mask, presence, cfg);
            _busy = true;
            _go.Set();
        }

        public void ClearGesture() { _held = null; }

        (double cx, double cy, double top, double bottom)? _me;
        double _meAt = -10, _meChecked = -10, _faceRetryAt, _scoreLogAt, _learnAt = -10;
        List<(double cx, double cy, double w, bool me)> _seenFaces = new();

        /// <summary>
        /// Find your face among all faces (and handle "remember me"). Your face is the one that looks like
        /// your saved fingerprints; one right where you just were only has to look roughly like you
        /// (turned head, bad light), one somewhere else has to look clearly like you.
        /// </summary>
        void FindMe(Config c, double now)
        {
            _meChecked = now;
            string here = AppContext.BaseDirectory;
            _faces ??= new FaceId(Path.Combine(here, "models", "face_detector.tflite"), Path.Combine(here, "models", "face_embedder.tflite"));
            var (rgb, rw, rh) = Nv12.SmallRgb(_buf, _w, _h, _zoom, 640);
            bool remember = _p.RememberRequested;
            _p.RememberRequested = false;
            List<FaceId.Face> faces;
            try { faces = _faces.Faces(rgb, rw, rh); }
            catch { if (remember) _p.OnRemembered?.Invoke(null); throw; }
            if (faces.Count > 0) _p.LastPerson = now;

            if (remember)
            {
                if (faces.Count == 0) _p.OnRemembered?.Invoke(null);
                else
                {
                    // the biggest face nearest the middle is you
                    var you = faces.OrderByDescending(f => f.W * f.H * (1.5 - Math.Abs((f.X + f.W / 2.0) / rw - 0.5))).First();
                    _lastFace = ((you.X + you.W / 2.0) / rw, (you.Y + you.H / 2.0) / rh);
                    _meAt = now;
                    _p.OnRemembered?.Invoke(you.Print);
                }
            }

            var (me, others) = c.Locked(() => (c.me_faces.ToList(), c.not_me_faces.ToList()));
            if (me.Count == 0) { _me = null; _seenFaces = new(); return; }
            FaceId.Face? best = null; double bestScore = 0;
            var scores = new List<double>();
            foreach (var f in faces)
            {
                double cx = (f.X + f.W / 2.0) / rw, cy = (f.Y + f.H / 2.0) / rh;
                double near = Math.Sqrt((cx - _lastFace.cx) * (cx - _lastFace.cx) + (cy - _lastFace.cy) * (cy - _lastFace.cy));
                double look = FaceId.MatchScore(f.Print, me);
                // a face that looks more like someone it has learned isn't you, however close it is
                if (others.Count > 0 && FaceId.MatchScore(f.Print, others) >= look) { scores.Add(look); continue; }
                double need = near < 0.12 && now - _meAt < 1 ? c.face_match - 0.1 : c.face_match;
                double score = look - need - 0.1 * near;
                if (look >= need && (best == null || score > bestScore)) { bestScore = score; best = f; }
                scores.Add(look);
            }
            if (scores.Count >= 2 && now - _scoreLogAt > 20)
            {
                // two or more faces in view: log how alike each is to you, to tune the match level
                _scoreLogAt = now;
                Log.Instance.Info("faces in view, likeness to you: " + string.Join(", ", scores.Select(x => x.ToString("0.00"))));
            }
            if (best is { } mine && faces.Count > 1 && now - _learnAt > 5)
            {
                // you and someone else at once: remember the other face as "not me" (only clear cases,
                // only if it isn't one already learned)
                foreach (var f in faces)
                {
                    if (ReferenceEquals(f.Print, mine.Print)) continue;
                    if (FaceId.MatchScore(f.Print, me) >= c.face_match - 0.1) continue;
                    if (others.Count > 0 && FaceId.MatchScore(f.Print, others) >= 0.9) continue;
                    _learnAt = now;
                    _p.OnLearnedOther?.Invoke(f.Print);
                    break;
                }
            }
            _seenFaces = faces.Select(f => ((f.X + f.W / 2.0) / rw, (f.Y + f.H / 2.0) / rh, (double)f.W / rw,
                                            best is { } bb && ReferenceEquals(f.Print, bb.Print))).ToList();
            if (best is { } b)
            {
                _lastFace = ((b.X + b.W / 2.0) / rw, (b.Y + b.H / 2.0) / rh);
                _me = (_lastFace.cx, _lastFace.cy, Math.Max(0, (b.Y - b.H * 0.6) / rh), Math.Min(1, (b.Y + b.H * 3.0) / rh));
                _meAt = now;
            }
            else _me = null;
        }

        /// <summary>Face lock: follow your face; nobody matching = hold still (never swing to someone else).</summary>
        void FollowMe(double now)
        {
            if (_me is { } m) { _p.Body = (m.cx, m.top, m.bottom, 0.1, now); _p.FaceFound = true; }
            else { _p.Body = (0.5, 0, 1, 0, now); _p.FaceFound = false; }
        }

        /// <summary>
        /// Keep only one person in the background mask: the outline your face is in (or, if your face
        /// hasn't been seen for a few seconds, the biggest outline). Everyone else gets blurred too.
        /// </summary>
        internal static float[] KeepOne(float[] m, int w, int h, (double x, double y)? seed)
        {
            var lab = new int[w * h];
            var sizes = new List<int> { 0 };
            var stack = new Stack<int>();
            for (int i = 0; i < m.Length; i++)
            {
                if (lab[i] != 0 || m[i] <= 0.25f) continue;
                int id = sizes.Count, n = 0;
                lab[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int p = stack.Pop(); n++;
                    int x = p % w, y = p / w;
                    if (x > 0 && lab[p - 1] == 0 && m[p - 1] > 0.25f) { lab[p - 1] = id; stack.Push(p - 1); }
                    if (x < w - 1 && lab[p + 1] == 0 && m[p + 1] > 0.25f) { lab[p + 1] = id; stack.Push(p + 1); }
                    if (y > 0 && lab[p - w] == 0 && m[p - w] > 0.25f) { lab[p - w] = id; stack.Push(p - w); }
                    if (y < h - 1 && lab[p + w] == 0 && m[p + w] > 0.25f) { lab[p + w] = id; stack.Push(p + w); }
                }
                sizes.Add(n);
            }
            if (sizes.Count <= 2) return m;                     // one person (or nobody): nothing to do
            int keep = 0;
            if (seed is { } sd)
            {
                // the outline under your face, or the nearest one within a short distance
                int sx = Math.Clamp((int)(sd.x * w), 0, w - 1), sy = Math.Clamp((int)(sd.y * h), 0, h - 1);
                int r = Math.Max(2, w / 10), bestD = int.MaxValue;
                for (int y = Math.Max(0, sy - r); y <= Math.Min(h - 1, sy + r); y++)
                    for (int x = Math.Max(0, sx - r); x <= Math.Min(w - 1, sx + r); x++)
                    {
                        int l = lab[y * w + x]; if (l == 0) continue;
                        int d = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                        if (d < bestD) { bestD = d; keep = l; }
                    }
            }
            if (keep == 0) for (int l = 1; l < sizes.Count; l++) if (keep == 0 || sizes[l] > sizes[keep]) keep = l;
            var o = new float[m.Length];
            for (int i = 0; i < m.Length; i++) o[i] = lab[i] == keep || lab[i] == 0 ? m[i] : 0;
            return o;
        }

        /// <summary>
        /// With your face saved, a hand sign only counts if the face nearest the hand is yours. Nobody's face
        /// matching you (or no face seen) = not yours either.
        /// </summary>
        bool MyHand(Config c, double now, (double x, double y)? hand)
        {
            if (!c.gestures_only_me || c.Locked(() => c.me_faces.Count) == 0) return true;
            if (hand is not { } h) return false;
            if (now - _meChecked > 0.3 && now >= _faceRetryAt)
            {
                try { FindMe(c, now); } catch (Exception e) { Log.Instance.Info("face check failed: " + e.Message); _faceRetryAt = now + 30; return false; }
            }
            if (_seenFaces.Count == 0) return false;
            // nearest face, measured in face widths; hands sit beside or below a face, so up/down counts half
            var nearest = _seenFaces.MinBy(f => Math.Sqrt((h.x - f.cx) * (h.x - f.cx) + 0.25 * (h.y - f.cy) * (h.y - f.cy)) / Math.Max(0.02, f.w));
            return nearest.me;
        }

        void Run()
        {
            string here = AppContext.BaseDirectory;
            var log = Log.Instance;
            double nextTry = 0;
            while (!_stop)
            {
                _go.WaitOne();
                if (_stop) break;
                double now = Now;
                try
                {
                    if (now < nextTry) continue;
                    var c = _cfg!;
                    bool lockOn = _presence && _p.FaceLock;
                    bool onlyMe = _mask && c.bg_only_me && c.bg_mode != "off" && c.Locked(() => c.me_faces.Count) > 0;
                    if ((lockOn || _p.RememberRequested || (onlyMe && now - _meChecked > 0.15)) && now >= _faceRetryAt)
                    {
                        try { FindMe(c, now); }
                        catch (Exception e)
                        {
                            log.Info("face check failed: " + e.Message);
                            _faceRetryAt = now + 30; _me = null;
                            if (_p.RememberRequested) { _p.RememberRequested = false; _p.OnRemembered?.Invoke(null); }
                        }
                    }
                    if (lockOn) FollowMe(now);
                    if (_mask)
                    {
                        _seg ??= new Segmenter(Path.Combine(here, "models", "selfie_segmenter.tflite"));
                        var (sr, sw, sh) = Nv12.SmallRgb(_buf, _w, _h, _zoom, 256);
                        var (m, mw, mh) = _seg.Mask(sr, sw, sh, (long)(now * 1000));
                        Mask = (onlyMe ? KeepOne(m, mw, mh, now - _meAt < 3 ? (_lastFace.cx, _lastFace.cy) : null) : m, mw, mh);
                        if (_presence)
                        {
                            var box = PersonBox(m, mw, mh);
                            if (box.area > 0.03) _p.LastPerson = now;
                            if (!_p.FaceLock) _p.Body = (box.cx, box.top, box.bottom, box.area, now);
                        }
                    }
                    if (_gesture)
                    {
                        _rec ??= new GestureRecognizer(Path.Combine(here, "models", "gesture_recognizer.task"));
                        var (rgb, rw, rh) = Nv12.SmallRgb(_buf, _w, _h, _zoom);
                        var (name, score, hand, handAt) = _rec.Recognize(rgb, rw, rh, (long)(now * 1000));
                        if (hand) _p.LastPerson = now;
                        string? seen = name != null && score >= c.min_score ? name : null;
                        if (seen == null) { _held = null; _needRelease = false; }
                        else if (!_needRelease && now >= _cooldownUntil)
                        {
                            if (seen != _held) { _held = seen; _heldSince = now; }
                            else
                            {
                                double hold = c.gesture_hold.TryGetValue(seen, out var hh) && hh > 0 ? hh : c.hold_s;
                                if (now - _heldSince >= hold)
                                {
                                    // whose hand is it? checked once, right when the sign would fire (not every picture)
                                    if (MyHand(c, now, handAt))
                                    {
                                        _p._onGesture(seen);
                                        _cooldownUntil = now + c.cooldown_s;
                                    }
                                    else
                                        Log.Instance.Info($"ignored {seen} from a hand that isn't yours (hand {(handAt is { } ha ? $"{ha.x:0.00},{ha.y:0.00}" : "?")}, faces " +
                                            string.Join(" ", _seenFaces.Select(f => $"{f.cx:0.00},{f.cy:0.00}{(f.me ? " (you)" : "")}")) + ")");
                                    _held = null; _needRelease = true;
                                }
                            }
                        }
                    }
                }
                catch (Exception e) { log.Info("AI check failed: " + e.Message); nextTry = now + 30; }
                finally { _busy = false; }
            }
        }

        public void Dispose()
        {
            _stop = true; _go.Set();
            if (_t.Join(3000)) { _rec?.Dispose(); _seg?.Dispose(); _faces?.Dispose(); }   // still busy: let the process clean up
        }
    }

    internal static float[] KeepOneForTest(float[] m, int w, int h, (double x, double y)? seed) => Ai.KeepOne(m, w, h, seed);

    /// <summary>Load a picture without keeping the file locked (so it can be moved or edited).</summary>
    public static Bitmap LoadBitmap(string path)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    /// <summary>Where the person is: centre x, top and bottom (0..1) and how much of the picture they fill.</summary>
    static (double cx, double top, double bottom, double area) PersonBox(float[] m, int w, int h)
    {
        long sumX = 0; int on = 0, top = h, bottom = -1;
        var rowOn = new int[h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (m[y * w + x] > 0.5f) { sumX += x; on++; rowOn[y]++; }
        if (on == 0) return (0.5, 0, 1, 0);
        int minRow = Math.Max(2, w / 40);                 // ignore stray specks
        for (int y = 0; y < h; y++) if (rowOn[y] >= minRow) { top = y; break; }
        for (int y = h - 1; y >= 0; y--) if (rowOn[y] >= minRow) { bottom = y; break; }
        if (bottom < top) { top = 0; bottom = h - 1; }
        return ((double)sumX / on / w, (double)top / h, (double)(bottom + 1) / h, (double)on / (w * h));
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
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        var key = $"{path}|{stamp}|{w}x{h}";
        if (_pics.Count > 6 && !_pics.ContainsKey(key)) _pics.Clear();     // only a handful are ever in use
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

/// <summary>Where the person is (fractions 0..1): centre x, top y, bottom y, share of picture, and when it was measured.</summary>
public sealed record BodyInfo(double cx, double top, double bottom, double area, double at)
{
    public static implicit operator BodyInfo((double cx, double top, double bottom, double area, double at) t) => new(t.cx, t.top, t.bottom, t.area, t.at);
}
