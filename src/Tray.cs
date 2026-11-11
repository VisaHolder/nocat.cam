using System.Drawing;
using System.Drawing.Drawing2D;

namespace NocatCam;

/// <summary>Tray icon (nocat logo + status dot) and its dark right-click menu.</summary>
public sealed class Tray : IDisposable
{
    readonly App _app;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _track, _sleep, _bg, _brb, _gest, _vcam;
    readonly Bitmap _logo;
    readonly Control _ui;
    string _lastLevel = "";
    public event Action? OpenSettings;
    public event Action? Quit;

    public Tray(App app, Control ui)
    {
        _app = app; _ui = ui;
        _logo = Pipeline.LoadBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "logo.png"));
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => OpenSettings?.Invoke()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        _track = Item(menu, "Tracking", () => _app.Run("toggle_tracking"));
        _sleep = Item(menu, "Asleep", () => _app.Run("toggle_sleep"));
        _bg = Item(menu, "Background effect", () => _app.Run("toggle_bg"));
        _brb = Item(menu, "Be right back", () => _app.Run("toggle_brb"));
        menu.Items.Add(new ToolStripMenuItem("Take a photo", null, (_, _) => _app.Run("snapshot")));
        menu.Items.Add(new ToolStripSeparator());
        _gest = Item(menu, "Gestures on", () => { _app.Cfg.gestures_enabled = !_app.Cfg.gestures_enabled; _app.Cfg.Save(); Refresh(); });
        _vcam = Item(menu, "Virtual camera", () => { _app.Cfg.virtual_cam = !_app.Cfg.virtual_cam; _app.Cfg.Save(); Refresh(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit?.Invoke()));
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "nocat.cam" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings?.Invoke(); };
        app.Changed += () => { if (_ui.IsHandleCreated) _ui.BeginInvoke(Refresh); };
        app.Notify += t => { if (app.Cfg.notifications && _ui.IsHandleCreated) _ui.BeginInvoke(() => _icon.ShowBalloonTip(2000, "nocat.cam", t, ToolTipIcon.None)); };
        Refresh();
    }

    static ToolStripMenuItem Item(ContextMenuStrip m, string text, Action click)
    {
        var i = new ToolStripMenuItem(text, null, (_, _) => click());
        m.Items.Add(i);
        return i;
    }

    void Refresh()
    {
        _track.Checked = _app.TrackingOn;
        _sleep.Checked = _app.Asleep;
        _bg.Checked = _app.Cfg.bg_mode != "off";
        _brb.Checked = _app.Brb;
        _gest.Checked = _app.Cfg.gestures_enabled;
        _vcam.Checked = _app.Cfg.virtual_cam;
        var (level, text) = _app.Status();
        var tip = "nocat.cam - " + text;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        if (level != _lastLevel) { _lastLevel = level; SetIcon(level); }
    }

    void SetIcon(string level)
    {
        var dot = level switch
        {
            "ok" => Color.FromArgb(80, 200, 120), "sleep" => Color.FromArgb(220, 70, 70),
            "warn" => Color.FromArgb(230, 170, 40), _ => Color.FromArgb(150, 150, 160),
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_logo, 0, 0, 32, 32);
            using var ring = new SolidBrush(Color.FromArgb(14, 14, 16));
            using var fill = new SolidBrush(dot);
            g.FillEllipse(ring, 19, 19, 13, 13);
            g.FillEllipse(fill, 21, 21, 9, 9);
        }
        var old = _icon.Icon;
        nint h = bmp.GetHicon();
        using (var tmp = Icon.FromHandle(h)) _icon.Icon = (Icon)tmp.Clone();   // own copy, then free the raw handle
        DestroyIcon(h);
        old?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32")] static extern bool DestroyIcon(nint h);

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _logo.Dispose(); }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Bg = Color.FromArgb(24, 24, 29), Hover = Color.FromArgb(40, 38, 58), Fg = Color.FromArgb(236, 236, 241);
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Fg; base.OnRenderItemText(e); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var b = new SolidBrush(e.Item.Selected ? Hover : Bg);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using var p = new Pen(Color.FromArgb(139, 123, 255), 2);
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(p, [new Point(r.Left + 3, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new Point(r.Right - 3, r.Top + 4)]);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Color.FromArgb(40, 40, 48));
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(24, 24, 29), Line = Color.FromArgb(40, 40, 48);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Bg;
        public override Color CheckPressedBackground => Bg;
    }
}
