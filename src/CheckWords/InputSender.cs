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

    /// <summary>
    /// Waits until the user releases Ctrl/Shift/Alt/Win, so they don't mix with our input
    /// (Backspace with Alt held is "Undo" in many editors). Returns false if they are still held
    /// after <paramref name="timeoutMs"/>: the caller must then do nothing.
    /// </summary>
    public static async Task<bool> WaitForModifiersReleased(int timeoutMs = 2000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Modifiers.AnyDown())
        {
            if (clock.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(15);
        }
        return true;
    }

    /// <summary>
    /// The hotkey's main key is swallowed, so the window sees only "Alt down, Alt up" and treats it
    /// as a lone Alt tap: it activates its menu bar and our Backspaces go there. Sending an
    /// unassigned key while Alt is still down cancels that (the same trick AutoHotkey uses).
    /// </summary>
    public static void MaskAltRelease()
    {
        const int unassignedVk = 0xE8;
        Send(new List<Native.INPUT> { Key(unassignedVk, up: false), Key(unassignedVk, up: true) });
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

/// <param name="Ctrl">A Ctrl key the user really holds (not the one Windows fakes for AltGr).</param>
/// <param name="Alt">Left or right Alt, including right Alt working as AltGr.</param>
/// <param name="AltGr">Right Alt in a layout with AltGr (e.g. Ukrainian Enhanced): types ґ etc.</param>
internal readonly record struct Modifiers(bool Ctrl, bool Shift, bool Alt, bool Win, bool AltGr)
{
    internal static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Ctrl/Alt/Win shortcut (not AltGr typing): the caret position is no longer known.</summary>
    public bool IsShortcut => Ctrl || Win || (Alt && !AltGr);

    public static bool AnyDown() =>
        Down(Vk.Control) || Down(Vk.Shift) || Down(Vk.Menu) || Down(Vk.LWin) || Down(Vk.RWin);
}

/// <summary>
/// Tells a real left Ctrl from the fake one Windows adds when right Alt acts as AltGr.
/// Without this, right Alt in the Ukrainian layout looks exactly like Ctrl+Alt.
/// The fake Ctrl has scan code 0x21D, or (as a fallback) the same timestamp as the right Alt press.
/// </summary>
internal sealed class ModifierTracker
{
    const int FakeCtrlScan = 0x21D;
    bool _leftCtrlReal;
    uint _leftCtrlTime;

    public void OnKeyDown(KeyInfo key)
    {
        if (key.Vk == Vk.LControl)
        {
            if (key.Scan == FakeCtrlScan || _leftCtrlReal) return; // fake, or auto-repeat of a held key
            _leftCtrlReal = true;
            _leftCtrlTime = key.Time;
        }
        else if (key.Vk == Vk.RMenu && _leftCtrlReal && key.Time == _leftCtrlTime)
        {
            _leftCtrlReal = false; // that "Ctrl" was generated together with AltGr
        }
    }

    public void OnKeyUp(KeyInfo key)
    {
        if (key.Vk == Vk.LControl && key.Scan != FakeCtrlScan) _leftCtrlReal = false;
    }

    public Modifiers Current()
    {
        bool leftCtrl = Modifiers.Down(Vk.LControl);
        bool realCtrl = (leftCtrl && _leftCtrlReal) || Modifiers.Down(Vk.RControl);
        bool rightAlt = Modifiers.Down(Vk.RMenu);
        return new Modifiers(
            Ctrl: realCtrl,
            Shift: Modifiers.Down(Vk.Shift),
            Alt: Modifiers.Down(Vk.LMenu) || rightAlt,
            Win: Modifiers.Down(Vk.LWin) || Modifiers.Down(Vk.RWin),
            AltGr: rightAlt && leftCtrl && !_leftCtrlReal);
    }
}
