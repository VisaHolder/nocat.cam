using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Global keybinds through Windows' own RegisterHotKey (no keyboard hook watching every key).
/// Combos are written like "alt+shift+1", "ctrl+f5", "windows+left".
/// </summary>
public sealed class Hotkeys : NativeWindow, IDisposable
{
    [DllImport("user32")] static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] static extern bool UnregisterHotKey(nint hwnd, int id);
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CTRL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly Action<string> _fire;
    readonly Dictionary<int, string> _ids = new();
    readonly SynchronizationContext? _ui;
    public List<string> Failed { get; } = new();    // combos another program already owns

    public Hotkeys(Action<string> fire)
    {
        _fire = fire;
        _ui = SynchronizationContext.Current;
        CreateHandle(new CreateParams { Parent = new nint(-3) });   // message-only window
    }

    public void Bind(Dictionary<string, string> map)
    {
        void Do()
        {
            Unbind();
            int id = 1;
            foreach (var (action, combo) in map)
            {
                if (string.IsNullOrWhiteSpace(combo)) continue;
                if (!TryParse(combo, out var mods, out var vk)) { Failed.Add(combo); continue; }
                if (RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk)) _ids[id++] = action;
                else Failed.Add(combo);
            }
        }
        if (_ui != null && SynchronizationContext.Current != _ui) _ui.Send(_ => Do(), null); else Do();
    }

    public void Unbind()
    {
        void Do()
        {
            foreach (var id in _ids.Keys) UnregisterHotKey(Handle, id);
            _ids.Clear();
            Failed.Clear();
        }
        if (_ui != null && SynchronizationContext.Current != _ui) _ui.Send(_ => Do(), null); else Do();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _ids.TryGetValue((int)m.WParam, out var action)) _fire(action);
        base.WndProc(ref m);
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw)
            {
                case "alt": mods |= MOD_ALT; continue;
                case "ctrl" or "control": mods |= MOD_CTRL; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "windows" or "win" or "meta": mods |= MOD_WIN; continue;
            }
            vk = KeyCode(raw);
            if (vk == 0) return false;
        }
        return vk != 0;
    }

    static uint KeyCode(string k)
    {
        if (k.Length == 1 && k[0] is >= '0' and <= '9') return (uint)k[0];
        if (k.Length == 1 && k[0] is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(k[0]);
        if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return k switch
        {
            "space" => 0x20, "enter" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B,
            "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "insert" => 0x2D, "delete" => 0x2E, "pause" => 0x13, "scrolllock" => 0x91,
            "-" or "minus" => 0xBD, "=" or "plus" => 0xBB, "[" => 0xDB, "]" => 0xDD, ";" => 0xBA,
            "'" => 0xDE, "," => 0xBC, "." => 0xBE, "/" => 0xBF, "\\" => 0xDC, "`" => 0xC0,
            _ when k.StartsWith("num") && k.Length == 4 && char.IsDigit(k[3]) => (uint)(0x60 + k[3] - '0'),
            _ => 0,
        };
    }

    public void Dispose() { Unbind(); DestroyHandle(); }
}
