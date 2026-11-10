using System.Text.Json;
using System.Text.Json.Nodes;

namespace NocatCam;

/// <summary>All settings. Stored as plain JSON in %APPDATA%\nocat.cam\config.json.</summary>
public sealed class Config
{
    public bool gestures_enabled { get; set; } = true;
    public string only_with_apps { get; set; } = "";
    public bool virtual_cam { get; set; } = true;
    public string vcam_size { get; set; } = "1920x1080";
    public string capture_size { get; set; } = "3840x2160";
    public bool fixed_exposure { get; set; }
    public double zoom { get; set; } = 1.0;
    public double zoom_step { get; set; } = 0.1;
    public double glide_s { get; set; } = 1.5;
    public double nudge_deg { get; set; } = 5.0;
    public string bg_mode { get; set; } = "off";      // off | blur | image
    public string bg_last { get; set; } = "blur";
    public int bg_blur { get; set; } = 6;
    public string bg_image { get; set; } = "";
    public string sleep_style { get; set; } = "privacy";  // privacy | soft
    public string wake_to { get; set; } = "stay";
    public double auto_sleep_away_min { get; set; }
    public double auto_sleep_unused_min { get; set; }
    public bool auto_wake_on_use { get; set; } = true;
    public double hold_s { get; set; } = 1.5;
    public Dictionary<string, double> gesture_hold { get; set; } = new();
    public double cooldown_s { get; set; } = 3.0;
    public double min_score { get; set; } = 0.6;
    public Dictionary<string, string> gesture_actions { get; set; } = new();
    public Dictionary<string, string> hotkeys { get; set; } = new();
    public Dictionary<string, double[]> positions { get; set; } = new();
    public bool notifications { get; set; } = true;

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nocat.cam");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    public static bool Exists => File.Exists(FilePath);

    public static Config Load()
    {
        Config c;
        try { c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new(); }
        catch { c = new(); }
        c.Fill();
        return c;
    }

    /// <summary>Defaults for anything missing, and clamp what a hand-edited file could break.</summary>
    void Fill()
    {
        foreach (var g in Gestures.Names.Keys)
            gesture_actions.TryAdd(g, g == "Closed_Fist" ? "sleep" : "none");
        if (hotkeys.Count == 0)
        {
            hotkeys["toggle_sleep"] = "alt+0";
            for (int i = 1; i <= 9; i++) { hotkeys[$"goto_{i}"] = $"alt+{i}"; hotkeys[$"save_{i}"] = $"alt+shift+{i}"; }
        }
        foreach (var k in Actions.HotkeyLabels.Keys) hotkeys.TryAdd(k, "");
        if (vcam_size is not ("3840x2160" or "2560x1440" or "1920x1080" or "1280x720")) vcam_size = "1920x1080";
        if (capture_size is not ("3840x2160" or "2560x1440" or "1920x1080")) capture_size = "3840x2160";
        zoom = Math.Clamp(zoom, 1, 3);
        hold_s = Math.Clamp(hold_s, 0.3, 10);
        min_score = Math.Clamp(min_score, 0.3, 0.95);
        bg_blur = Math.Clamp(bg_blur, 1, 10);
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
    }

    /// <summary>Change one setting by name (from the settings page). Returns false for unknown names.</summary>
    public bool Set(string key, JsonNode? value)
    {
        var p = typeof(Config).GetProperty(key);
        if (p == null || p.PropertyType.IsGenericType) return false;   // dictionaries have their own calls
        object? v = p.PropertyType == typeof(bool) ? value?.GetValue<bool>()
                  : p.PropertyType == typeof(int) ? (int)Math.Round(value!.GetValue<double>())
                  : p.PropertyType == typeof(double) ? value!.GetValue<double>()
                  : value?.GetValue<string>() ?? "";
        lock (Gate) p.SetValue(this, v);
        Fill();
        return true;
    }

    public JsonNode ToJson() => JsonSerializer.SerializeToNode(this)!;
}
