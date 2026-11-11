using System.Diagnostics;
using Microsoft.Win32;

namespace NocatCam;

/// <summary>
/// The virtual camera driver. nocat.cam carries OBS Studio's virtual camera module (two small DLLs, GPL-2.0,
/// unmodified), so OBS doesn't have to be installed. Installing copies them to
/// %ProgramData%\nocat.cam\vcam (a folder that doesn't move when nocat.cam updates), registers them with
/// Windows and names the camera "nocat.cam". If OBS's own copy is already registered, that one is used.
/// </summary>
public static class VcamDriver
{
    const string Clsid = "{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
    const string VideoInputCategory = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}";
    static readonly string[] Dlls = ["obs-virtualcam-module64.dll", "obs-virtualcam-module32.dll"];

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "nocat.cam", "vcam");
    static string Bundled => Path.Combine(AppContext.BaseDirectory, "vcam");

    /// <summary>The DLL Windows loads for the virtual camera, or null if none is registered.</summary>
    public static string? RegisteredPath()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
            return k?.GetValue(null) as string;
        }
        catch { return null; }
    }

    /// <summary>"nocat" (our copy), "obs" (OBS's copy), "missing", or "broken" (registered file is gone).</summary>
    public static string State()
    {
        var p = RegisteredPath();
        if (string.IsNullOrEmpty(p)) return "missing";
        if (!File.Exists(p)) return "broken";
        return p.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) ? "nocat" : "obs";
    }

    public static bool Ready => State() is "nocat" or "obs";

    /// <summary>Ask Windows for admin once and install (runs this exe with --install-vcam). True when it worked.</summary>
    public static bool Install()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-vcam") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(60000);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }      // said no to the admin prompt
        return Ready;
    }

    /// <summary>The admin part (exit code 0 = ok).</summary>
    public static int InstallElevated()
    {
        var log = Log.Instance;
        try
        {
            if (State() == "obs") { Rename("nocat.cam"); log.Info("virtual camera: using OBS's, renamed"); return 0; }
            Directory.CreateDirectory(Dir);
            foreach (var d in Dlls)
            {
                var src = Path.Combine(Bundled, d);
                if (!File.Exists(src)) throw new FileNotFoundException("missing from this copy of nocat.cam", d);
                File.Copy(src, Path.Combine(Dir, d), true);
            }
            // the picture apps see while nocat.cam isn't sending (the driver looks for it next to itself)
            var card = Path.Combine(AppContext.BaseDirectory, "assets", "card_idle.png");
            if (File.Exists(card)) File.Copy(card, Path.Combine(Dir, "placeholder.png"), true);
            foreach (var d in Dlls) RegSvr(Path.Combine(Dir, d), uninstall: false);
            Rename("nocat.cam");
            log.Info("virtual camera installed: " + RegisteredPath());
            return State() == "nocat" ? 0 : 1;
        }
        catch (Exception e) { log.Info("virtual camera install failed: " + e.Message); return 1; }
    }

    public static int UninstallElevated()
    {
        if (State() != "nocat") return 0;                    // never touch OBS's own copy
        foreach (var d in Dlls) RegSvr(Path.Combine(Dir, d), uninstall: true);
        return 0;
    }

    static void RegSvr(string dll, bool uninstall)
    {
        var args = (uninstall ? "/u /s " : "/i /s ") + "\"" + dll + "\"";
        using var p = Process.Start(new ProcessStartInfo("regsvr32.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(30000);
        if (p.ExitCode != 0) throw new Exception($"regsvr32 {Path.GetFileName(dll)} failed ({p.ExitCode})");
    }

    /// <summary>What apps like Discord list the camera as (64-bit and 32-bit apps).</summary>
    static void Rename(string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var root in new[] { @"SOFTWARE\Classes", @"SOFTWARE\WOW6432Node\Classes" })
        {
            using (var inst = hklm.OpenSubKey($@"{root}\CLSID\{VideoInputCategory}\Instance\{Clsid}", true))
                inst?.SetValue("FriendlyName", name);
            using (var cls = hklm.OpenSubKey($@"{root}\CLSID\{Clsid}", true))
                cls?.SetValue(null, name);
        }
    }
}
