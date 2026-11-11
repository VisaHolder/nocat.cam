using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NocatCam;

/// <summary>
/// The settings window: the settings page (ui\index.html) in WebView2. The window and its browser
/// engine only exist while it's open - closing it frees all of that memory.
/// </summary>
public sealed class SettingsForm : Form
{
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    readonly App _app;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(13, 13, 16) };

    public SettingsForm(App app)
    {
        _app = app;
        Text = "nocat.cam settings";
        BackColor = Color.FromArgb(13, 13, 16);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = LogicalToDeviceUnits(new Size(980, 700));
        MinimumSize = LogicalToDeviceUnits(new Size(840, 580));
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "assets", "logo.ico"), 32, 32);
        Controls.Add(_web);
        Load += async (_, _) => await Init();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // dark title bar
    }

    async Task Init()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nocat.cam", "webview");
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        await _web.EnsureCoreWebView2Async(env);
        var cw = _web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = false;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
        cw.SetVirtualHostNameToFolderMapping("nocat.cam.local", Path.Combine(AppContext.BaseDirectory, "ui"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        cw.AddHostObjectToScript("nocat", new Bridge(_app, this));
        cw.Navigate("https://nocat.cam.local/index.html");
    }

    /// <summary>Save a picture of every settings page into <paramref name="dir"/> (for the README).</summary>
    public async Task Shoot(string dir)
    {
        while (_web.CoreWebView2 == null) await Task.Delay(100);
        ClientSize = LogicalToDeviceUnits(new Size(980, 760));
        await Task.Delay(2500);                                   // page loaded and first status in
        Directory.CreateDirectory(dir);
        foreach (var p in new[] { "control", "positions", "gestures", "keys", "picture", "sleep", "general" })
        {
            await _web.CoreWebView2.ExecuteScriptAsync($"document.querySelector('a[data-p=\"{p}\"]').click()");
            await Task.Delay(1200);
            using var f = File.Create(Path.Combine(dir, p + ".png"));
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
        }
    }

    public string? PickPicture()
    {
        using var d = new OpenFileDialog { Title = "Background picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
        return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _app.Keys.Bind(_app.Cfg.hotkeys);     // in case a keybind recording was left half-done
        _web.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>What the settings page can call: window.chrome.webview.hostObjects.nocat.Call(name, argsJson).</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class Bridge
{
    readonly App _app;
    readonly SettingsForm _form;
    internal Bridge(App app, SettingsForm form) { _app = app; _form = form; }

    public string Call(string name, string argsJson)
    {
        try
        {
            var args = JsonNode.Parse(string.IsNullOrEmpty(argsJson) ? "[]" : argsJson)!.AsArray();
            JsonNode? r = Dispatch(name, args);
            return r?.ToJsonString() ?? "null";
        }
        catch (Exception e)
        {
            Log.Instance.Info($"settings call {name} failed: {e.Message}");
            return "null";
        }
    }

    static string S(JsonArray a, int i) => a[i]?.GetValue<string>() ?? "";

    JsonNode? Dispatch(string name, JsonArray a)
    {
        var c = _app.Cfg;
        switch (name)
        {
            case "get_state":
                return new JsonObject
                {
                    ["cfg"] = c.ToJson(),
                    ["actions"] = JsonSerializer.SerializeToNode(Actions.All),
                    ["gestures"] = JsonSerializer.SerializeToNode(Gestures.Names),
                    ["hotkey_labels"] = JsonSerializer.SerializeToNode(Actions.HotkeyLabels),
                    ["startup"] = App.StartupEnabled,
                    ["camera_sizes"] = JsonSerializer.SerializeToNode(Capture.LastSizes),
                    ["version"] = Application.ProductVersion.Split('+')[0],
                };
            case "status":
            {
                var (level, text) = _app.Status();
                var v = _app.Video;
                if (v.Streaming && c.virtual_cam) text += v.HasReaders ? $" · {v.Fps:0} fps" : " · idle";
                return new JsonObject
                {
                    ["level"] = level switch { "ok" => "ok", "sleep" => "bad", "warn" => "warn", _ => "" },
                    ["text"] = text,
                    ["tracking"] = _app.TrackingOn,
                    ["asleep"] = _app.Asleep,
                    ["zoom"] = c.zoom,
                    ["faces"] = c.Locked(() => c.me_faces.Count),
                    ["others"] = c.Locked(() => c.not_me_faces.Count),
                    ["update"] = Updater.State,
                    ["face_found"] = v.FaceLock && !_app.Asleep && _app.TrackingOn ? v.FaceFound : (bool?)null,
                    ["positions"] = c.Locked(() => JsonSerializer.SerializeToNode(c.positions)),
                    ["hotkeys_failed"] = JsonSerializer.SerializeToNode(_app.Keys.Failed),
                };
            }
            case "set":
            {
                var key = S(a, 0);
                if (key == "bg_mode" && S(a, 1) != "off") c.bg_last = S(a, 1);
                if (!c.Set(key, a[1]?.DeepClone())) return false;
                c.Save();
                if (key == "fixed_exposure") _app.ApplyExposure(c.fixed_exposure);
                if (key == "track_style" && _app.TrackingOn) _app.Run("tracking");   // switch style straight away
                return c.ToJson();                       // the page re-reads: changing one setting can adjust another
            }
            case "set_gesture":
                c.Locked(() => c.gesture_actions[S(a, 0)] = Actions.All.ContainsKey(S(a, 1)) ? S(a, 1) : "none");
                c.Save(); return true;
            case "set_gesture_hold":
            {
                double v = a[1]?.GetValue<double>() ?? 0;
                c.Locked(() => { if (v > 0) c.gesture_hold[S(a, 0)] = Math.Clamp(v, 0.3, 10); else c.gesture_hold.Remove(S(a, 0)); });
                c.Save(); return true;
            }
            case "pause_hotkeys": _app.Keys.Unbind(); return true;
            case "set_hotkey":
            {
                var combo = S(a, 1);
                if (combo != "" && !Hotkeys.TryParse(combo, out _, out _)) { _app.Keys.Bind(c.hotkeys); return false; }
                c.Locked(() => c.hotkeys[S(a, 0)] = combo);
                c.Save();
                _app.Keys.Bind(c.hotkeys);
                return !_app.Keys.Failed.Contains(combo);
            }
            case "resume_hotkeys": _app.Keys.Bind(c.hotkeys); return true;
            case "action": _app.Run(S(a, 0)); return true;
            case "get_auto_privacy": return _app.Cam.GetAutoPrivacy();
            case "set_auto_privacy": return _app.Cam.SetAutoPrivacy((int)a[0]!.GetValue<double>());
            case "save_position": return JsonSerializer.SerializeToNode(_app.SavePosition(S(a, 0)));
            case "clear_position": c.Locked(() => c.positions.Remove(S(a, 0))); c.Save(); return true;
            case "check_update":
            {
                var r = Task.Run(Updater.Latest).GetAwaiter().GetResult();
                return new JsonObject { ["current"] = Updater.Current, ["latest"] = r?.Version, ["newer"] = r != null && Updater.IsNewer(r.Version) };
            }
            case "install_update": _ = Task.Run(() => Updater.Install(_app)); return true;
            case "open_github":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/" + Updater.Repo) { UseShellExecute = true });
                return true;
            case "set_startup": App.StartupEnabled = a[0]!.GetValue<bool>(); return true;
            case "pick_bg":
            {
                var p = _form.PickPicture();
                if (p == null) return null;
                c.bg_image = p; c.bg_mode = "image"; c.bg_last = "image"; c.Save();
                return p;
            }
            case "pic_info":
            {
                var cap = _app.Video.Camera;
                // the camera lives on a background (MTA) thread; asking from this window's thread fails, so ask from one too
                return cap == null ? new JsonObject() : JsonSerializer.SerializeToNode(Task.Run(() => PictureControls.Read(cap)).GetAwaiter().GetResult());
            }
            case "pic_set":
            {
                var cap = _app.Video.Camera;
                if (cap != null) { string n = S(a, 0); int v = (int)a[1]!.GetValue<double>(); bool au = a[2]!.GetValue<bool>(); Task.Run(() => PictureControls.Set(cap, n, v, au)); }
                return true;
            }
            case "pic_reset":
            {
                var cap = _app.Video.Camera;
                string rn = S(a, 0);
                return cap == null ? null : JsonSerializer.SerializeToNode(Task.Run(() => PictureControls.Reset(cap, rn)).GetAwaiter().GetResult());
            }
        }
        return null;
    }
}
