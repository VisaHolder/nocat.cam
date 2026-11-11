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
    /// <summary>nocat.cam is steering the camera to keep your whole body in frame (camera in manual).</summary>
    public bool BodyTracking { get; private set; }
    public bool TrackingOn => BodyTracking || Mode == Pixy.Tracking;
    int _modeBeforeSleep = Pixy.Manual;
    double _lastActivity = Now;

    /// <summary>Raised (on any thread) when tray ticks / icon should be refreshed.</summary>
    public event Action? Changed;
    public event Action<string>? Notify;

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double Now => Clock.Elapsed.TotalSeconds;

    public App()
    {
        Video = new Pipeline(() => Cfg, ShouldWatch, () => SoftAsleep, () => CamAsleep,
            g => { _log.Info($"gesture {g} held"); Run(Cfg.gesture_actions.GetValueOrDefault(g, "none")); },
            s => { _log.Info("status: " + s); Changed?.Invoke(); });
        Keys = new Hotkeys(a => Run(a));
        Video.CameraOpened += () => { if (Cfg.fixed_exposure) ApplyExposure(true); };
    }

    public void Start()
    {
        Video.Start();
        Keys.Bind(Cfg.hotkeys);
        new Thread(PollMode) { IsBackground = true, Name = "mode" }.Start();
        new Thread(AutoLoop) { IsBackground = true, Name = "auto" }.Start();
        new Thread(BodyLoop) { IsBackground = true, Name = "body" }.Start();
        if (Cfg.fixed_exposure) ApplyExposure(true);
    }

    bool ShouldWatch()
    {
        if (!Cfg.gestures_enabled || CamAsleep) return false;   // in privacy mode it can't see you
        var wanted = Cfg.only_with_apps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0) return true;
        return RunningPrograms().Overlaps(wanted.Select(w => w.ToLowerInvariant().Replace(".exe", "")));
    }

    static (double at, HashSet<string> names) _procCache = (-10, new());
    static HashSet<string> RunningPrograms()
    {
        if (Now - _procCache.at < 3) return _procCache.names;
        var names = Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }).ToHashSet();
        _procCache = (Now, names);
        return names;
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
            if (Asleep) return;
            var m = Cam.GetMode();
            _modeBeforeSleep = m is Pixy.Manual or Pixy.Tracking ? m.Value : Pixy.Manual;
            if (c.sleep_style == "soft" || why == "away") SoftAsleep = true;   // away: stay soft to see you come back
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
            case "zoom_in" or "zoom_out" or "zoom_reset":
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
        if (Cfg.track_style == "body") { Cam.SetMode(Pixy.Manual); BodyTracking = true; }
        else { BodyTracking = false; Cam.SetMode(Pixy.Tracking); }
    }

    /// <summary>
    /// Whole-body tracking: 5 times a second, look where the person's outline is and turn the camera so
    /// they're centred left-right with the top of their head near the top of the picture. Because it
    /// follows the whole outline (not just a face), crouching or turning away doesn't lose you.
    /// </summary>
    void BodyLoop()
    {
        const double HFov = 70, VFov = 42;      // PIXY field of view (degrees, roughly) at zoom 1
        const double DeadX = 0.08, DeadY = 0.07, Gain = 0.55, MaxStep = 12;
        double lastMeasured = 0, movedAt = 0;
        while (!_quit)
        {
            Thread.Sleep(100);
            Video.WantBody = BodyTracking && !Asleep;
            if (!BodyTracking || Asleep) continue;
            var b = Video.Body;
            double now = Now;
            // only act on a fresh measurement taken after the camera finished its last move
            if (b.at <= lastMeasured || now - b.at > 0.8 || b.at < movedAt + 0.35) continue;
            lastMeasured = b.at;
            if (b.area < 0.03) continue;           // nobody there: hold still
            double zoom = Math.Max(1, Cfg.zoom);
            double ex = b.cx - 0.5;
            double ey = b.top - 0.12;               // head just below the top edge
            double dPan = Math.Abs(ex) > DeadX ? Math.Clamp(-ex * HFov / zoom * Gain, -MaxStep, MaxStep) : 0;
            double dTilt = Math.Abs(ey) > DeadY ? Math.Clamp(-ey * VFov / zoom * Gain, -MaxStep, MaxStep) : 0;
            if (dPan == 0 && dTilt == 0) continue;
            if (!Monitor.TryEnter(_doLock)) continue;
            try
            {
                var pos = Cam.Position();
                if (pos == null) continue;
                Cam.MoveTo(pos.Value.pan + dPan, Math.Clamp(pos.Value.tilt + dTilt, -60, 60));
                movedAt = Now;
            }
            finally { Monitor.Exit(_doLock); }
        }
    }

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
        if (pos.Length > 2) Cfg.zoom = pos[2];
        Cam.GlideTo(pos[0], pos[1], Cfg.glide_s);
    }

    public double[]? SavePosition(string slot)
    {
        var pos = Cam.Position();
        if (pos == null) { Notify?.Invoke("Couldn't read the camera position"); return null; }
        var p = new[] { Math.Round(pos.Value.pan, 3), Math.Round(pos.Value.tilt, 3), Cfg.zoom };
        Cfg.positions[slot] = p;
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
            int? m;
            try { m = Cam.GetMode(); } finally { Monitor.Exit(_doLock); }
            if (m != null && m != Mode)
            {
                Mode = m;
                CamAsleep = m == Pixy.Sleep;
                if (m == Pixy.Tracking) BodyTracking = false;   // the palm gesture switched to the camera's own tracking
                if (!CamAsleep && AutoSlept != null && !SoftAsleep) AutoSlept = null;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Sleep when you've walked away or nothing uses nocat.cam; wake when that changes.</summary>
    void AutoLoop()
    {
        double unusedSince = Now;
        bool hadUsers = false;
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
                    startedUsing = users.Count > 0 && !hadUsers;
                    hadUsers = users.Count > 0;
                }

                if (AutoSlept == "away" && now - Video.LastPerson < 6) { Do("wake", "you're back"); continue; }
                if (Asleep && users is { Count: > 0 } && (AutoSlept == "unused" || (startedUsing && c.auto_wake_on_use)))
                { Do("wake", "an app started using nocat.cam"); continue; }
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
