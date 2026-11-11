using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NocatCam;

/// <summary>
/// Updates from GitHub releases (VisaHolder/nocat.cam). A new version is unpacked into its own folder
/// next to this one (nocatcam-1.0.4 -> nocatcam-1.0.5), then started; the old copy closes and stays
/// on disk as a backup. Settings live in %APPDATA% so they carry over.
/// </summary>
public static class Updater
{
    public const string Repo = "VisaHolder/nocat.cam";
    public static string Current => Application.ProductVersion.Split('+')[0];
    /// <summary>What the updater is doing, for the settings page ("" = nothing).</summary>
    public static volatile string State = "";
    static int _busy;
    static Control? _ui;                 // UI-thread anchor that lives as long as the app

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("nocat.cam");
        return h;
    }

    public record Release(string Version, string ZipUrl, string? Sha256);

    public static async Task<Release?> Latest()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
        using var d = JsonDocument.Parse(json);
        var tag = d.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
        foreach (var a in d.RootElement.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : null;
            return new Release(tag, a.GetProperty("browser_download_url").GetString()!, sha);
        }
        return null;
    }

    public static bool IsNewer(string v) =>
        Version.TryParse(v, out var a) && Version.TryParse(Current, out var b) && a > b;

    /// <summary>Download and unpack next to the running copy (or into <paramref name="parent"/>). Returns the new exe.</summary>
    public static async Task<string> Download(Release r, string? parent = null)
    {
        string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        parent ??= Path.GetDirectoryName(here)!;
        string name = Path.GetFileName(here);
        string newName = name.EndsWith("-" + Current) ? name[..^Current.Length] + r.Version : "nocat.cam-" + r.Version;
        string dest = Path.Combine(parent, newName);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(here), StringComparison.OrdinalIgnoreCase))
            throw new Exception("already on " + r.Version);

        string zip = Path.Combine(Path.GetTempPath(), $"nocat.cam-{r.Version}-{Environment.ProcessId}.zip");
        string part = dest + ".part";
        try
        {
            using (var s = await Http.GetStreamAsync(r.ZipUrl).ConfigureAwait(false))
            using (var f = File.Create(zip)) await s.CopyToAsync(f).ConfigureAwait(false);
            if (r.Sha256 != null)
            {
                using var f = File.OpenRead(zip);
                var got = Convert.ToHexString(SHA256.HashData(f));
                if (!got.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception("download was damaged (checksum)");
            }
            if (Directory.Exists(part)) Directory.Delete(part, true);
            ZipFile.ExtractToDirectory(zip, part);
            var exe = Directory.GetFiles(part, "nocat.cam.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new Exception("no nocat.cam.exe in the download");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);      // a half-finished earlier try
            Directory.Move(Path.GetDirectoryName(exe)!, dest);
            return Path.Combine(dest, "nocat.cam.exe");
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            try { if (Directory.Exists(part)) Directory.Delete(part, true); } catch { }
        }
    }

    /// <summary>Check, download and switch to the new version. Returns false if there's nothing newer.</summary>
    public static async Task<bool> Install(App app, bool onlyIfNewer = true)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return true;
        try
        {
            State = "checking";
            var r = await Latest().ConfigureAwait(false);
            if (r == null || (onlyIfNewer && !IsNewer(r.Version))) { State = ""; return false; }
            State = "downloading " + r.Version;
            app.Say($"Downloading nocat.cam {r.Version}...");
            var exe = await Download(r).ConfigureAwait(false);
            Log.Instance.Info($"update: {Current} -> {r.Version} unpacked to {Path.GetDirectoryName(exe)}");
            State = "restarting";
            Process.Start(new ProcessStartInfo(exe, $"--updated-from {Environment.ProcessId}")
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            if (_ui != null) _ui.BeginInvoke(Application.Exit); else Environment.Exit(0);
            return true;
        }
        catch (Exception e)
        {
            Log.Instance.Info("update failed: " + e.Message);
            State = "failed: " + e.Message;
            app.Say("Update failed: " + e.Message);
            return true;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Background: check an hour after start, then every 6 hours; installs only while no app is using nocat.cam.</summary>
    public static void StartAuto(App app, Control ui)
    {
        _ui = ui;
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));
            while (true)
            {
                try
                {
                    if (app.Cfg.auto_update)
                    {
                        var r = Latest().GetAwaiter().GetResult();
                        if (r != null && IsNewer(r.Version))
                        {
                            Log.Instance.Info($"update {r.Version} found; waiting until nothing uses the camera");
                            while (app.InUse) Thread.Sleep(TimeSpan.FromMinutes(1));   // never cut off a call
                            if (app.Cfg.auto_update) Install(app).GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception e) { Log.Instance.Info("update check failed: " + e.Message); }
                Thread.Sleep(TimeSpan.FromHours(6));
            }
        }) { IsBackground = true, Name = "updates" }.Start();
    }
}
