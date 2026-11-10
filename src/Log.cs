namespace NocatCam;

/// <summary>Small log file next to the settings (%APPDATA%\nocat.cam\nocatcam.log), capped at ~0.5 MB.</summary>
public sealed class Log
{
    public static readonly Log Instance = new();
    readonly string _path = Path.Combine(Config.Dir, "nocatcam.log");
    readonly object _lock = new();
    string? _last;

    public void Info(string msg)
    {
        lock (_lock)
        {
            if (msg == _last) return;            // no repeats
            _last = msg;
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 512_000) File.Move(_path, _path + ".old", true);
                File.AppendAllText(_path, $"{DateTime.Now:MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
