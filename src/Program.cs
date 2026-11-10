namespace NocatCam;

static class Program
{
    const string ShowEvent = "nocat.cam.show-settings";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") { Environment.Exit(SelfTest.Run(args[1])); return; }

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
