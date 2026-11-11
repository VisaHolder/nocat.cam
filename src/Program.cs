namespace NocatCam;

static class Program
{
    const string ShowEvent = "nocat.cam.show-settings";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") { Environment.Exit(SelfTest.Run(args[1])); return; }
        if (args.Length >= 2 && args[0] == "--speedtest")
        {
            int rc = 0;
            var t = new Thread(() => rc = SpeedTest.Run(args[1]));
            if (Environment.GetEnvironmentVariable("NOCAT_STA") != "1") t.SetApartmentState(ApartmentState.MTA);
            else t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            Environment.Exit(rc); return;
        }
        if (args.Length >= 2 && args[0] == "--switchtest") { Environment.Exit(SwitchTest.Run(args[1])); return; }
        if (args.Length >= 1 && args[0] == "--auto-exposure")
        {
            // repair tool: put the camera back on automatic exposure
            using var cap = Capture.Open("EMEET PIXY", 1920, 1080, 30, out _);
            if (cap == null) { Environment.Exit(1); return; }
            bool done = false;                       // the camera only takes changes while pictures flow
            var reader = new Thread(() => { var b = new byte[1 << 20]; while (!done) cap.Read(ref b, false); });
            reader.Start();
            for (int i = 0; i < 10; i++) Thread.Sleep(100);
            bool ok = PictureControls.Set(cap, "Exposure", -5, auto: true);
            done = true; reader.Join();
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        using var single = new Mutex(true, "nocat.cam.single", out bool first);
        if (!first)
        {
            // already running: opening it again = open its settings
            try { EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        // never show .NET's crash dialog: log it and keep going (the tray stays usable)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Instance.Info("error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Instance.Info("fatal: " + e.ExceptionObject);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        // a config.json placed next to the exe is imported once (moving settings between PCs / versions)
        var import = Path.Combine(AppContext.BaseDirectory, "import-config.json");
        if (File.Exists(import))
        {
            try { Directory.CreateDirectory(Config.Dir); File.Copy(import, Config.FilePath, true); File.Delete(import); } catch { }
        }
        bool firstRun = !Config.Exists;

        using var app = new App();
        if (App.StartupEnabled) App.StartupEnabled = true;     // keep the start-with-Windows entry pointing at this exe

        var ui = new Control();                               // UI-thread anchor for Invoke
        ui.CreateControl();
        _ = ui.Handle;

        SettingsForm? settings = null;
        void ShowSettings()
        {
            if (settings is { IsDisposed: false }) { settings.WindowState = FormWindowState.Normal; settings.Activate(); return; }
            settings = new SettingsForm(app);
            settings.FormClosed += (_, _) => settings = null;
            settings.Show();
        }

        using var tray = new Tray(app, ui);
        tray.OpenSettings += ShowSettings;
        tray.Quit += () => Application.Exit();

        // listen for a second launch asking us to show settings
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() => { while (show.WaitOne()) ui.BeginInvoke(ShowSettings); }) { IsBackground = true }.Start();

        app.Start();
        Log.Instance.Info($"nocat.cam {Application.ProductVersion.Split('+')[0]} started");
        if (firstRun || args.Contains("--settings")) { app.Cfg.Save(); ShowSettings(); }

        Application.Run();
    }
}
