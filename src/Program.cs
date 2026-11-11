namespace NocatCam;

static class Program
{
    const string ShowEvent = "nocat.cam.show-settings";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--boosttest") { Environment.Exit(BoostTest.Run(args[1])); return; }
        if (args.Length >= 1 && args[0] == "--install-vcam") { Environment.Exit(VcamDriver.InstallElevated()); return; }
        if (args.Length >= 1 && args[0] == "--uninstall-vcam") { Environment.Exit(VcamDriver.UninstallElevated()); return; }
        if (args.Length >= 3 && args[0] == "--cpubench") { Environment.Exit(CpuBench.Run(args[1], args[2])); return; }
        if (args.Length >= 2 && args[0] == "--facetest") { Environment.Exit(FaceTest.Run(args[1])); return; }
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

        int fromIdx = Array.IndexOf(args, "--updated-from");
        if (fromIdx >= 0 && fromIdx + 1 < args.Length && int.TryParse(args[fromIdx + 1], out int oldPid))
        {
            // just updated: let the old copy close (and let go of the camera) first
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(20000); } catch { }
        }
        if (args.Length >= 2 && args[0] == "--updatetest")
        {
            // download the latest release into a scratch folder (no restart) to prove the updater works
            try
            {
                var r = Updater.Latest().GetAwaiter().GetResult() ?? throw new Exception("no release with a zip");
                var exe = Updater.Download(r, args[1]).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(args[1], "updatetest.txt"), $"PASS {r.Version} sha256 {(r.Sha256 != null ? "checked" : "missing")} -> {exe} ({new FileInfo(exe).Length} bytes, version {System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion})");
                Environment.Exit(0);
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(args[1], "updatetest.txt"), "FAIL " + e.Message); Environment.Exit(1); }
            return;
        }

        Mutex single;
        bool first;
        for (int i = 0; ; i++)
        {
            single = new Mutex(true, "nocat.cam.single", out first);
            if (first || fromIdx < 0 || i >= 20) break;
            single.Dispose(); Thread.Sleep(500);              // old copy still shutting down
        }
        using var _single = single;
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

        // a "take-screenshots" file next to the exe = save pictures of every settings page into .\shots
        var shotWatch = new FileSystemWatcher(AppContext.BaseDirectory, "take-screenshots") { EnableRaisingEvents = true };
        shotWatch.Created += (_, _) => ui.BeginInvoke(async () =>
        {
            try { File.Delete(Path.Combine(AppContext.BaseDirectory, "take-screenshots")); } catch { }
            ShowSettings();
            try { await settings!.Shoot(Path.Combine(AppContext.BaseDirectory, "shots")); Log.Instance.Info("settings pictures saved"); }
            catch (Exception e) { Log.Instance.Info("settings pictures failed: " + e.Message); }
            settings?.Close();
        });

        app.Start();
        Log.Instance.Info($"nocat.cam {Application.ProductVersion.Split('+')[0]} started");
        // first start, or the camera driver isn't installed yet: open settings (it shows an Install banner)
        if (firstRun || args.Contains("--settings") || !VcamDriver.Ready) { app.Cfg.Save(); ShowSettings(); }
        if (fromIdx >= 0) app.Say($"Updated to {Updater.Current}");
        if (!VcamDriver.Ready) app.Say("One more step: open settings, General, and press Install next to Camera driver");
        Updater.StartAuto(app, ui);

        Application.Run();
    }
}
