using System.Diagnostics;
using Microsoft.Win32;

namespace NocatCam;

/// <summary>The brain: actions, sleep/wake, auto-sleep, keybinds. Camera commands run one at a time.</summary>
public sealed class App : IDisposable
{
    public Config Cfg { get; } = Config.Load();
    public Pixy Cam { get; } = new();
    public Pipeline Video { get; }
    public Hotkeys Keys { get; }
    readonly object _doLock = new();
    readonly Log _log = Log.Instance;
    volatile bool _quit;

    public bool CamAsleep { get; private set; }     // camera itself in privacy mode
    public bool SoftAsleep { get; private set; }    // camera awake, apps see the asleep card
    public string? AutoSlept { get; private set; }  // "away" / "unused" when we put it to sleep ourselves
    public int? Mode { get; private set; }
    public bool Asleep => CamAsleep || SoftAsleep;
    /// <summary>An app (Discord, OBS...) is showing nocat.cam right now.</summary>
    public bool InUse => Cfg.virtual_cam && Video.HasReaders;
    public bool Brb => Video.Brb;
    /// <summary>When you last changed the zoom yourself: auto framing waits a bit after that.</summary>
    public double ManualZoomAt = -100;
    public void Say(string text) => Notify?.Invoke(text);
    /// <summary>nocat.cam is steering the camera to keep your whole body in frame (camera in manual).</summary>
    bool _bodyTracking;
    /// <summary>nocat.cam's own tracking (Whole body / Only me). Remembered, so it comes back after a restart or update.</summary>
    public bool BodyTracking
    {
        get => _bodyTracking;
        private set
        {
            if (_bodyTracking == value) return;
            _bodyTracking = value;
            if (!value) { Video.AutoZoom = 0; _frameRef = 0; _faceAvg = 0; }   // back to your own zoom; re-measure next time
            if (Cfg.body_tracking != value) { Cfg.body_tracking = value; Cfg.Save(); }
        }
    }
    public bool TrackingOn => BodyTracking || Mode == Pixy.Tracking;
    int _modeBeforeSleep = Pixy.Manual;
    double _lastActivity = Now;

    /// <summary>Raised (on any thread) when tray ticks / icon should be refreshed.</summary>
    public event Action? Changed;
    public event Action<string>? Notify;

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;

    public App()
    {
        Video = new Pipeline(() => Cfg, ShouldWatch, () => SoftAsleep, () => CamAsleep,
            g =>
            {
                _log.Info($"gesture {g} held");
                var act = Cfg.gesture_actions.GetValueOrDefault(g, "none");
                if (act == "keys")
                {
                    var combo = Cfg.Locked(() => Cfg.gesture_keys.GetValueOrDefault(g, ""));
                    if (!string.IsNullOrEmpty(combo)) { bool ok = KeyPress.Send(combo); _log.Info($"pressed {combo}{(ok ? "" : " (Windows refused)")}"); }
                }
                else Run(act);
            },
            s => { _log.Info("status: " + s); Changed?.Invoke(); });
        Keys = new Hotkeys(a => Run(a));
        Video.CameraOpened += () => { if (Cfg.fixed_exposure) ApplyExposure(true); };
        Video.OnSnapshot += path => { _log.Info("photo saved: " + (path ?? "failed")); Notify?.Invoke(path != null ? "Photo saved to Pictures\\nocat.cam" : "Couldn't take the photo"); };
        Video.OnLearnedOther += print =>
        {
            Cfg.Locked(() =>
            {
                Cfg.not_me_faces.Add(print);
                while (Cfg.not_me_faces.Count > 30) Cfg.not_me_faces.RemoveAt(0);
                // a "not me" face that looks like you would be a mistake: never keep one that matches a saved you
                Cfg.not_me_faces.RemoveAll(o => FaceId.MatchScore(o, Cfg.me_faces) >= Cfg.face_match + 0.25);
            });
            Cfg.Save();
            _log.Info($"learned someone else's face ({Cfg.not_me_faces.Count} saved)");
        };
        Video.OnRemembered += print =>
        {
            if (print == null) { Notify?.Invoke("Couldn't see a face - look at the camera and try again"); return; }
            Cfg.Locked(() =>
            {
                Cfg.me_faces.Add(print); while (Cfg.me_faces.Count > 12) Cfg.me_faces.RemoveAt(0);
                Cfg.not_me_faces.RemoveAll(o => FaceId.Similarity(o, print) >= Cfg.face_match + 0.1);
            });
            Cfg.Save();
            Notify?.Invoke($"Remembered your face ({Cfg.me_faces.Count} looks saved)");
            _log.Info($"face remembered ({Cfg.me_faces.Count} saved)");
        };
    }

    public void Start()
    {
        Video.Start();
        Keys.Bind(Cfg.hotkeys);
        new Thread(PollMode) { IsBackground = true, Name = "mode" }.Start();
        new Thread(AutoLoop) { IsBackground = true, Name = "auto" }.Start();
        new Thread(BodyLoop) { IsBackground = true, Name = "body" }.Start();
        if (Cfg.fixed_exposure) ApplyExposure(true);
        if (Cfg.body_tracking && Cfg.track_style is "body" or "me") Run("tracking", "was on before");
    }

    bool ShouldWatch()
    {
        if (!Cfg.gestures_enabled || CamAsleep) return false;   // in privacy mode it can't see you
        var wanted = Cfg.only_with_apps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0) return true;
        return RunningPrograms().Overlaps(wanted.Select(w => w.ToLowerInvariant().Replace(".exe", "")));
    }

    static (double at, HashSet<string> names) _procCache = (-10, ProcessNames());
    static int _procBusy;
    static HashSet<string> ProcessNames() => Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }).ToHashSet();
    static HashSet<string> RunningPrograms()
    {
        if (Now - _procCache.at >= 3 && Interlocked.Exchange(ref _procBusy, 1) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    var names = Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }).ToHashSet();
                    _procCache = (Now, names);
                }
                finally { Interlocked.Exchange(ref _procBusy, 0); }
            });
        }
        return _procCache.names;
    }

    // ---- actions --------------------------------------------------------------------
    /// <summary>Run an action on a worker thread (keeps keybinds and the UI snappy).</summary>
    public void Run(string action, string? why = null) => ThreadPool.QueueUserWorkItem(_ => Do(action, why));

    public void Do(string action, string? why = null)
    {
        if (action == "none") return;
        if (why == null) _lastActivity = Now;
        lock (_doLock)
        {
            try { DoLocked(action, why); }
            catch (Exception e) { _log.Info($"action {action} failed: {e.Message}"); }
            var m = Cam.GetMode();
            if (m != null) { Mode = m; CamAsleep = m == Pixy.Sleep; }
        }
        Changed?.Invoke();
    }

    void DoLocked(string action, string? why)
    {
        _log.Info("action " + action + (why != null ? $" ({why})" : ""));
        var c = Cfg;
        if (action == "toggle_sleep") action = Cam.GetMode() == Pixy.Sleep || SoftAsleep ? "wake" : "sleep";
        if (action == "toggle_tracking") action = BodyTracking || Cam.GetMode() == Pixy.Tracking ? "manual" : "tracking";
        if (action == "toggle_bg")
        {
            if (c.bg_mode == "off") c.bg_mode = c.bg_last;
            else { c.bg_last = c.bg_mode; c.bg_mode = "off"; }
            c.Save();
            return;
        }

        if (action == "sleep")
        {
            // nobody using nocat.cam: a soft sleep goes all the way down (camera turns away, light off)
            if (why == "unused" && c.unused_sleep_style == "privacy" && SoftAsleep && !CamAsleep)
            {
                if (Cam.SetMode(Pixy.Sleep)) { CamAsleep = true; SoftAsleep = false; AutoSlept = why; }
                return;
            }
            if (Asleep) return;
            var m = Cam.GetMode();
            _modeBeforeSleep = m is Pixy.Manual or Pixy.Tracking ? m.Value : Pixy.Manual;
            string style = why == "unused" ? c.unused_sleep_style : c.sleep_style;
            if (style == "soft" || why == "away") SoftAsleep = true;   // away: stay soft to see you come back
            else if (Cam.SetMode(Pixy.Sleep)) CamAsleep = true;
            AutoSlept = why;
            return;
        }
        if (action == "wake")
        {
            bool wasCamAsleep = Cam.GetMode() == Pixy.Sleep;
            SoftAsleep = false; AutoSlept = null;
            if (c.wake_to == "tracking") StartTracking();
            else if (c.positions.TryGetValue(c.wake_to, out var pos))
            {
                Cam.SetMode(Pixy.Manual);
                if (wasCamAsleep) Thread.Sleep(1500);      // let it finish lifting up before moving
                GoTo(pos);
            }
            else if (wasCamAsleep) Cam.SetMode(_modeBeforeSleep);
            return;
        }

        // anything else counts as "I'm here": wake from a soft sleep first
        if (SoftAsleep && !action.StartsWith("zoom") && !action.StartsWith("save_")) { SoftAsleep = false; AutoSlept = null; }

        // moving the camera by hand ends nocat.cam's body tracking
        if (action.StartsWith("goto_") || action.StartsWith("nudge_") || action == "center") BodyTracking = false;

        switch (action)
        {
            case "tracking": StartTracking(); break;
            case "manual": BodyTracking = false; Cam.SetMode(Pixy.Manual); break;
            case "toggle_brb":
                Video.Brb = !Video.Brb;
                Notify?.Invoke(Video.Brb ? "Be right back is on" : "Be right back is off");
                break;
            case "snapshot":
                if (Asleep || !Video.Streaming) { Notify?.Invoke("Wake the camera first to take a photo"); break; }
                Video.SnapshotRequested = true;
                break;
            case "remember_me":
                if (!Cfg.virtual_cam) { Notify?.Invoke("Turn the nocat.cam virtual camera on first"); break; }
                if (Asleep) { Notify?.Invoke("Wake the camera first"); break; }
                if (!Video.HasReaders) { Notify?.Invoke("Open nocat.cam in Discord (or any app) first, so it can see you"); break; }
                Video.RememberRequested = true;
                break;
            case "forget_me": Cfg.Locked(() => { Cfg.me_faces.Clear(); Cfg.not_me_faces.Clear(); }); Cfg.Save(); Notify?.Invoke("Forgot your face"); break;
            case "zoom_in" or "zoom_out" or "zoom_reset":
                ManualZoomAt = Now;
                c.zoom = action == "zoom_reset" ? 1 : Math.Round(Math.Clamp(c.zoom + (action == "zoom_in" ? c.zoom_step : -c.zoom_step), 1, 3), 2);
                c.Save();
                break;
            default:
                if (action.StartsWith("goto_"))
                {
                    if (c.positions.TryGetValue(action[5..], out var p)) { Cam.SetMode(Pixy.Manual); GoTo(p); }
                    else Notify?.Invoke($"Position {action[5..]} isn't saved yet");
                }
                else if (action.StartsWith("save_")) SavePosition(action[5..]);
                else if (action.StartsWith("nudge_") || action == "center") Nudge(action);
                break;
        }
    }

    /// <summary>Tracking on, in the chosen style: the camera's own face tracking, or nocat.cam's whole-body tracking.</summary>
    void StartTracking()
    {
        if (Cfg.track_style is "body" or "me" && Cfg.virtual_cam) { Cam.SetMode(Pixy.Manual); BodyTracking = true; }
        else { BodyTracking = false; Cam.SetMode(Pixy.Tracking); }
    }

    /// <summary>
    /// Whole-body tracking: 5 times a second, look where the person's outline is and turn the camera so
    /// they're centred left-right with the top of their head near the top of the picture. Because it
    /// follows the whole outline (not just a face), crouching or turning away doesn't lose you.
    /// </summary>
    /// <summary>
    /// nocat.cam's own tracking, as one continuous glide: about 16 times a second the camera is nudged
    /// towards a target, speeding up and slowing down smoothly (no stop-start). Each new measurement of
    /// where you are moves the target, measured against where the camera was pointing when that picture
    /// was taken. Small wobbles are ignored; it only sets off when you're clearly off-centre, then centres
    /// you properly.
    /// </summary>
    void BodyLoop()
    {
        const double HFov = 70, VFov = 42;          // PIXY field of view (degrees, roughly) at zoom 1
        const double StartX = 0.11, StartY = 0.10;  // how far off-centre before it sets off
        const double Tick = 0.06;
        double lastMeasured = 0;
        double cmdPan = 0, cmdTilt = 0, tgtPan = 0, tgtTilt = 0, velPan = 0, velTilt = 0;
        double tvPan = 0, tvTilt = 0, lastNPan = 0, lastNTilt = 0, lastNAt = -1;   // how fast you're moving (predicted between readings)
        bool have = false, moving = false;
        var history = new Queue<(double t, double pan, double tilt)>();   // where we pointed it, and when
        while (!_quit)
        {
            Thread.Sleep((int)(Tick * 1000));
            Video.WantBody = BodyTracking && !Asleep;
            Video.FaceLock = BodyTracking && Cfg.track_style == "me";      // no saved face yet = hold still
            Video.AutoFrame = BodyTracking && Cfg.auto_frame;
            if (!Cfg.auto_frame) { Video.AutoZoom = 0; _frameRef = 0; }
            if (!BodyTracking || Asleep) { have = false; moving = false; tvPan = tvTilt = 0; lastNAt = -1; history.Clear(); continue; }
            double now = Now;
            int speed = Cfg.follow_speed;
            double maxVel = 8 + 4 * speed;          // degrees a second at full speed (5 -> 28)
            double accel = 30 + 12 * speed;         // how quickly it gets up to speed

            if (!have)
            {
                var p = Cam.Position();
                if (p == null) continue;
                (cmdPan, cmdTilt) = p.Value; (tgtPan, tgtTilt) = p.Value; velPan = velTilt = 0; have = true;
            }

            // ---- a new measurement moves the target
            var b = Video.Body;
            if (b.at > lastMeasured && now - b.at < 0.8)
            {
                lastMeasured = b.at;
                if (b.area >= 0.03)
                {
                    if (b.head > 0 || b.span > 0) AutoFrame(b, now);
                    // where the camera was pointing when that picture was taken (pictures reach us ~0.1 s late)
                    double when = b.at - 0.1, atPan = cmdPan, atTilt = cmdTilt;
                    foreach (var h in history) if (h.t <= when) { atPan = h.pan; atTilt = h.tilt; }
                    double zoom = Math.Max(1, b.z > 0 ? b.z : Video.ZoomNow);
                    double ex = b.cx - 0.5, ey = b.top - 0.12;          // head just below the top edge
                    bool far = Math.Abs(ex) > StartX || Math.Abs(ey) > StartY;
                    bool centred = Math.Abs(ex) < 0.04 && Math.Abs(ey) < 0.04;
                    if (far || moving)
                    {
                        double nPan = atPan - ex * HFov / zoom, nTilt = Math.Clamp(atTilt - ey * VFov / zoom, -60, 60);
                        if (moving && lastNAt > 0 && b.at - lastNAt > 0.05)
                        {
                            // how fast you're moving, so the glide carries on between readings instead of stop-start
                            double k = 1 / (b.at - lastNAt);
                            tvPan += ((nPan - lastNPan) * k - tvPan) * 0.4;
                            tvTilt += ((nTilt - lastNTilt) * k - tvTilt) * 0.4;
                        }
                        (lastNPan, lastNTilt, lastNAt) = (nPan, nTilt, b.at);
                        // follow the measurement, but calmly: half way to each new reading
                        tgtPan += (nPan - tgtPan) * 0.5; tgtTilt += (nTilt - tgtTilt) * 0.5;
                        moving = true;
                        // you're centred, it has caught up and you've stopped: rest until you're clearly off-centre again
                        if (centred && Math.Abs(tgtPan - cmdPan) < 0.5 && Math.Abs(tgtTilt - cmdTilt) < 0.5 && Math.Abs(tvPan) < 2 && Math.Abs(tvTilt) < 2)
                        { moving = false; tvPan = tvTilt = 0; lastNAt = -1; }
                    }
                }
            }

            // ---- glide the camera towards the target (which keeps moving with you between readings)
            if (!moving) { velPan = velTilt = 0; continue; }
            if (now - lastMeasured > 0.5 || Video.Body.area < 0.03)
            {
                // lost you: stop predicting, glide to a halt, then rest
                tvPan *= 0.8; tvTilt *= 0.8;
                if (now - lastMeasured > 1.5 || Video.Body.area < 0.03) { tgtPan = cmdPan + velPan * 0.15; tgtTilt = cmdTilt + velTilt * 0.15; tvPan = tvTilt = 0; }
                if (Math.Abs(velPan) < 1 && Math.Abs(velTilt) < 1 && now - lastMeasured > 1.5) { moving = false; lastNAt = -1; continue; }
            }
            tgtPan += Math.Clamp(tvPan, -maxVel * 0.8, maxVel * 0.8) * Tick;
            tgtTilt = Math.Clamp(tgtTilt + Math.Clamp(tvTilt, -maxVel * 0.8, maxVel * 0.8) * Tick, -60, 60);
            double dp = tgtPan - cmdPan, dt = tgtTilt - cmdTilt;
            if (Math.Abs(dp) < 0.1 && Math.Abs(dt) < 0.1 && Math.Abs(velPan) < 1 && Math.Abs(velTilt) < 1) { velPan = velTilt = 0; continue; }   // on target: no need to send anything
            velPan = Steer(velPan, dp, maxVel, accel, Tick);
            velTilt = Steer(velTilt, dt, maxVel, accel, Tick);
            cmdPan += velPan * Tick; cmdTilt = Math.Clamp(cmdTilt + velTilt * Tick, -60, 60);
            if (!Monitor.TryEnter(_doLock)) continue;
            try { Cam.MoveTo(cmdPan, cmdTilt); }
            finally { Monitor.Exit(_doLock); }
            history.Enqueue((now, cmdPan, cmdTilt));
            while (history.Count > 40) history.Dequeue();
        }
    }

    /// <summary>
    /// Speed for the next step: aim for a speed that brakes in time to stop on the target
    /// (v = sqrt(2*a*distance)), never faster than max, changing speed by at most accel per second.
    /// </summary>
    static double Steer(double vel, double dist, double maxVel, double accel, double tick)
    {
        double want = Math.Sign(dist) * Math.Min(maxVel, Math.Sqrt(2 * accel * 0.8 * Math.Abs(dist)));
        double dv = Math.Clamp(want - vel, -accel * tick, accel * tick);
        return vel + dv;
    }

    /// <summary>
    /// Auto framing: keeps your face the size it is at the zoom you chose. The first measurement after
    /// tracking starts (or 20 s after you zoom by hand) is remembered; leaning back zooms in, coming closer
    /// zooms out. With two or more people (Whole body) it zooms out until everyone fits.
    /// </summary>
    void AutoFrame(BodyInfo b, double now)
    {
        if (!Cfg.auto_frame || b.z <= 0) return;
        if (now - ManualZoomAt < 20) { Video.AutoZoom = 0; _frameRef = 0; _faceAvg = 0; return; }   // you just zoomed yourself
        double z = Video.AutoZoom > 0 ? Video.AutoZoom : Math.Max(1, Cfg.zoom);
        double want;
        if (b.span > 0) want = b.z * 0.55 / b.span;            // a group: faces spread over about half the width
        else if (b.head > 0)
        {
            double raw = b.head / b.z;                          // how big your face is with no zoom
            _faceAvg = _faceAvg <= 0 ? raw : _faceAvg + (raw - _faceAvg) * 0.2;   // averaged over about a second
            double atOne = _faceAvg;
            if (_frameRef <= 0)
            {
                _frameRef = atOne * Math.Max(1, Cfg.zoom);
                _log.Info($"auto framing: keeping your face at {_frameRef * 100:0}% of the picture height");
            }
            want = _frameRef / atOne;
        }
        else return;
        if (b.head > 0) want = Math.Min(want, 0.6 / Math.Max(0.05, _faceAvg));   // never so close the face gets cut off
        want = Math.Clamp(want, 1, 3);
        if (Math.Abs(want - z) / z < 0.12) { if (Video.AutoZoom <= 0) Video.AutoZoom = z; return; }   // close enough: don't breathe
        if (Math.Abs(want - _frameLogged) > 0.25) { _frameLogged = want; _log.Info($"auto framing: zoom {z:0.00} -> {want:0.00}"); }
        Video.AutoZoom = Math.Round(want, 3);                 // the picture eases there smoothly
    }
    double _frameRef, _frameLogged, _faceAvg;

    void Nudge(string action)
    {
        var pos = Cam.Position();
        if (pos == null) return;
        var (pan, tilt) = pos.Value;
        double step = Cfg.nudge_deg;
        switch (action)
        {
            case "center": pan = 0; tilt = 0; break;
            case "nudge_left": pan += step; break;
            case "nudge_right": pan -= step; break;
            case "nudge_up": tilt += step; break;
            case "nudge_down": tilt -= step; break;
        }
        if (Cam.GetMode() != Pixy.Manual) Cam.SetMode(Pixy.Manual);
        if (action == "center") Cam.GlideTo(pan, tilt, Cfg.glide_s); else Cam.MoveTo(pan, tilt);
    }

    void GoTo(double[] pos)
    {
        if (Cfg.positions_zoom && pos.Length > 2) Cfg.zoom = pos[2];   // off by default: keep the zoom you're using
        Cam.GlideTo(pos[0], pos[1], Cfg.glide_s);
    }

    public double[]? SavePosition(string slot)
    {
        var pos = Cam.Position();
        if (pos == null) { Notify?.Invoke("Couldn't read the camera position"); return null; }
        var p = new[] { Math.Round(pos.Value.pan, 3), Math.Round(pos.Value.tilt, 3), Cfg.zoom };
        Cfg.Locked(() => Cfg.positions[slot] = p);
        Cfg.Save();
        Notify?.Invoke($"Saved position {slot}");
        return p;
    }

    public void ApplyExposure(bool fixedExposure) => ThreadPool.QueueUserWorkItem(_ =>
    {
        for (int i = 0; i < 20 && Video.Camera == null; i++) Thread.Sleep(500);
        var cap = Video.Camera;
        if (cap == null) return;
        try
        {
            var info = PictureControls.Read(cap);
            if (!info.TryGetValue("Exposure", out var e)) return;
            // 2^-5 s = 1/32 s fits 30 fps; 60 fps needs 2^-6 s = 1/64 s
            int fixedValue = Cfg.fps == 60 ? -6 : -5;
            PictureControls.Set(cap, "Exposure", fixedExposure ? fixedValue : e.value, auto: !fixedExposure);
            _log.Info(fixedExposure ? $"exposure fixed for a steady {Cfg.fps} fps" : "exposure back on auto");
        }
        catch (Exception ex) { _log.Info("exposure change failed: " + ex.Message); }
    });

    // ---- background loops -----------------------------------------------------------
    void PollMode()
    {
        while (!_quit)
        {
            Thread.Sleep(2000);
            if (!Monitor.TryEnter(_doLock)) continue;   // a command is running; don't read half-way
            bool changed = false;
            try
            {
                var m = Cam.GetMode();
                if (m != null && m != Mode)
                {
                    Mode = m;
                    CamAsleep = m == Pixy.Sleep;
                    if (m == Pixy.Tracking) BodyTracking = false;   // the palm gesture switched to the camera's own tracking
                    if (!CamAsleep && AutoSlept != null && !SoftAsleep) AutoSlept = null;
                    changed = true;
                }
            }
            finally { Monitor.Exit(_doLock); }
            if (changed) Changed?.Invoke();
        }
    }

    /// <summary>Sleep when you've walked away or nothing uses nocat.cam; wake when that changes.</summary>
    void AutoLoop()
    {
        double unusedSince = Now;
        var seen = new Dictionary<int, double>();          // app -> when it last used nocat.cam
        HashSet<int>? atSleep = null;                      // apps that were using it when it went to sleep
        while (!_quit)
        {
            Thread.Sleep(3000);
            try
            {
                var c = Cfg;
                double now = Now;
                HashSet<int>? users = null;
                bool startedUsing = false;
                if (c.virtual_cam)
                {
                    users = VcamUsers.Readers();
                    Video.HasReaders = users.Count > 0;     // nobody watching: send 2 pictures a second
                    if (users.Count > 0) unusedSince = now;
                    foreach (var u in users) seen[u] = now;
                    foreach (var k in seen.Where(kv => now - kv.Value > 120).Select(kv => kv.Key).ToList()) seen.Remove(k);
                }
                // Apps that were already using nocat.cam when it fell asleep don't wake it: Discord lets go of a
                // dark camera and grabs it again, which used to look like "an app started using it".
                if (Asleep && atSleep == null) atSleep = seen.Where(kv => now - kv.Value < 30).Select(kv => kv.Key).ToHashSet();
                if (!Asleep) atSleep = null;
                // an app that has been gone for a minute counts as new when it comes back (left the call, rejoined)
                atSleep?.RemoveWhere(u => !seen.TryGetValue(u, out var t) || now - t > 60);
                if (users != null && atSleep != null) startedUsing = users.Any(u => !atSleep.Contains(u));

                if (AutoSlept == "away" && now - Video.LastPerson < 6) { Do("wake", "you're back"); continue; }
                if (Asleep && startedUsing && (AutoSlept == "unused" || c.auto_wake_on_use))
                { Do("wake", "an app started using nocat.cam"); continue; }
                bool unused = c.auto_sleep_unused_min > 0 && users != null && now - Math.Max(unusedSince, Math.Max(_lastActivity, Video.OpenedAt)) > c.auto_sleep_unused_min * 60;
                if (unused && SoftAsleep && !CamAsleep && c.unused_sleep_style == "privacy") { Do("sleep", "unused"); continue; }
                if (Asleep) continue;

                // timers count from the latest of: last seen / last used, your last action, camera (re)opened
                double fresh = Math.Max(_lastActivity, Video.OpenedAt);
                if (c.auto_sleep_away_min > 0 && Video.Streaming && now - Math.Max(Video.LastPerson, fresh) > c.auto_sleep_away_min * 60)
                    Do("sleep", "away");
                else if (c.auto_sleep_unused_min > 0 && users != null && now - Math.Max(unusedSince, fresh) > c.auto_sleep_unused_min * 60)
                    Do("sleep", "unused");
            }
            catch (Exception e) { _log.Info("auto sleep check failed: " + e.Message); }
        }
    }

    // ---- status ----------------------------------------------------------------------
    /// <summary>(level, text): level is ok / sleep / warn / idle.</summary>
    public (string level, string text) Status()
    {
        if (Asleep) return ("sleep", "asleep");
        var s = Video.Status;
        if (s == "watching") return ("ok", "watching for gestures");
        if (s.Contains("in use") || s.Contains("busy") || s.Contains("not found") || s.StartsWith("couldn't")) return ("warn", s);
        return ("idle", s);
    }

    // ---- start with Windows -------------------------------------------------------------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartupEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("nocat.cam") != null; }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (value) k.SetValue("nocat.cam", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("nocat.cam", false);
        }
    }

    public void Dispose()
    {
        _quit = true;
        Keys.Dispose();
        Video.Stop();
        Cam.Dispose();
    }
}
