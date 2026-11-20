using System.Runtime.InteropServices;
using CheckWords.Core;

namespace CheckWords;

/// <summary>Types into the focused window with SendInput.</summary>
internal static class InputSender
{
    /// <summary>Marks our own synthetic input so the hook never feeds it back into the session.</summary>
    public static readonly IntPtr Signature = new(0x434B5744); // "CKWD"

    /// <summary>Deletes <paramref name="backspaces"/> chars and types <paramref name="text"/> in one batch.</summary>
    public static void Replace(int backspaces, string text)
    {
        var inputs = new List<Native.INPUT>(backspaces * 2 + text.Length * 2);
        for (int i = 0; i < backspaces; i++) AddKey(inputs, Vk.Back);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r': continue;
                case '\n': AddKey(inputs, Vk.Return); continue;
                case '\t': AddKey(inputs, Vk.Tab); continue;
            }
            inputs.Add(Unicode(c, up: false));
            inputs.Add(Unicode(c, up: true));
        }
        Send(inputs);
    }

    public static void CtrlC()
    {
        var inputs = new List<Native.INPUT>
        {
            Key(Vk.Control, up: false), Key(Vk.C, up: false), Key(Vk.C, up: true), Key(Vk.Control, up: true),
        };
        Send(inputs);
    }

    /// <summary>Waits (up to ~1.5 s) until the user releases Ctrl/Shift/Alt/Win, so they don't mix with our input.</summary>
    public static async Task WaitForModifiersReleased()
    {
        for (int i = 0; i < 100 && Modifiers.AnyDown(); i++) await Task.Delay(15);
    }

    static void AddKey(List<Native.INPUT> inputs, int vk)
    {
        inputs.Add(Key(vk, up: false));
        inputs.Add(Key(vk, up: true));
    }

    static Native.INPUT Key(int vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0, dwExtraInfo = Signature },
        },
    };

    static Native.INPUT Unicode(char c, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wScan = c,
                dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Signature,
            },
        },
    };

    static void Send(List<Native.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }
}

/// <summary>Physical state of the modifier keys right now.</summary>
internal readonly record struct Modifiers(bool Ctrl, bool Shift, bool Alt, bool Win, bool AltGr)
{
    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static Modifiers Current()
    {
        bool altGr = Down(Vk.RMenu) && Down(Vk.LControl);
        return new Modifiers(
            Ctrl: Down(Vk.Control) && !altGr,
            Shift: Down(Vk.Shift),
            Alt: Down(Vk.Menu) && !altGr,
            Win: Down(Vk.LWin) || Down(Vk.RWin),
            AltGr: altGr);
    }

    public static bool AnyDown() =>
        Down(Vk.Control) || Down(Vk.Shift) || Down(Vk.Menu) || Down(Vk.LWin) || Down(Vk.RWin);
}
