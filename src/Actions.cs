namespace NocatCam;

public static class Actions
{
    /// <summary>Everything a gesture can do (id -> label shown in settings).</summary>
    public static readonly Dictionary<string, string> All = Build();

    /// <summary>Everything a keybind can do (adds "save position N").</summary>
    public static readonly Dictionary<string, string> HotkeyLabels = BuildHotkeys();

    static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>
        {
            ["none"] = "Do nothing",
            ["sleep"] = "Sleep",
            ["wake"] = "Wake up",
            ["toggle_sleep"] = "Sleep / wake",
            ["tracking"] = "Tracking on",
            ["manual"] = "Tracking off",
            ["toggle_tracking"] = "Tracking on / off",
            ["nudge_left"] = "Move left",
            ["nudge_right"] = "Move right",
            ["nudge_up"] = "Move up",
            ["nudge_down"] = "Move down",
            ["center"] = "Move to centre",
            ["zoom_in"] = "Zoom in",
            ["zoom_out"] = "Zoom out",
            ["zoom_reset"] = "Zoom reset",
            ["toggle_bg"] = "Background effect on / off",
            ["remember_me"] = "Remember my face (for Only me)",
            ["toggle_brb"] = "Be right back on / off",
            ["snapshot"] = "Take a photo",
            ["keys"] = "Press keys…",
        };
        for (int i = 1; i <= 9; i++) d[$"goto_{i}"] = $"Go to position {i}";
        return d;
    }

    static Dictionary<string, string> BuildHotkeys()
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in Build())
            if (k is not ("none" or "sleep" or "wake" or "tracking" or "manual" or "keys")) d[k] = v;
        for (int i = 1; i <= 9; i++) d[$"save_{i}"] = $"Save position {i}";
        return d;
    }
}

public static class Gestures
{
    /// <summary>MediaPipe gesture names -> labels. Open palm is left out on purpose:
    /// the camera already uses it for its own tracking on/off.</summary>
    public static readonly Dictionary<string, string> Names = new()
    {
        ["Closed_Fist"] = "Closed fist",
        ["Thumb_Up"] = "Thumbs up",
        ["Thumb_Down"] = "Thumbs down",
        ["Victory"] = "Peace sign",
        ["Pointing_Up"] = "Pointing up",
        ["ILoveYou"] = "Rock on (I love you)",
    };
}
