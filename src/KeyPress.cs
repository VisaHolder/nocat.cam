using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Presses a key combo for a gesture ("ctrl+shift+m"): holds the modifiers, taps the key, lets go,
/// exactly like typing it. Works in any app that's listening, e.g. Discord's mute keybind or OBS's scene keys.
/// </summary>
public static class KeyPress
{
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    // INPUT is a union; the keyboard member is padded to the size of the mouse member (32 bytes on x64)
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32")] static extern uint MapVirtualKey(uint code, uint mapType);

    const uint KEYUP = 2, EXTENDED = 1;
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    public static bool Send(string combo)
    {
        if (!Hotkeys.TryParse(combo, out var mods, out var vk)) return false;
        var down = new List<ushort>();
        if ((mods & 2) != 0) down.Add(VK_CONTROL);
        if ((mods & 1) != 0) down.Add(VK_MENU);
        if ((mods & 4) != 0) down.Add(VK_SHIFT);
        if ((mods & 8) != 0) down.Add(VK_LWIN);
        down.Add((ushort)vk);
        var list = new List<INPUT>();
        foreach (var k in down) list.Add(Key(k, false));
        for (int i = down.Count - 1; i >= 0; i--) list.Add(Key(down[i], true));
        var arr = list.ToArray();
        return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) == arr.Length;
    }

    static INPUT Key(ushort vk, bool up)
    {
        bool ext = vk is >= 0x21 and <= 0x2E or 0x5B or 0x5C;      // arrows, home/end, page keys, insert/delete, windows
        return new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT
            {
                wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (up ? KEYUP : 0) | (ext ? EXTENDED : 0),
            },
        };
    }
}
