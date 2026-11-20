using System.ComponentModel;
using System.Runtime.InteropServices;
using CheckWords.Core;

namespace CheckWords;

/// <summary>One low-level keyboard event: virtual key, hardware scan code, LLKHF_* flags, timestamp (ms).</summary>
internal readonly record struct KeyInfo(int Vk, int Scan, uint Flags, uint Time);

/// <summary>
/// Global low-level keyboard and mouse hooks. Callbacks run on the thread that installed the hooks
/// (the UI thread) and must return quickly, so heavy work is posted back to the message loop.
/// Injected input (including our own SendInput) is ignored.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    readonly Native.LowLevelProc _keyboardProc;
    readonly Native.LowLevelProc _mouseProc;
    IntPtr _keyboardHook;
    IntPtr _mouseHook;

    public InputHooks()
    {
        // Keep the delegates in fields: the GC must not collect them while Windows holds pointers.
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>
    /// Also handle input injected by other programs (set CHECKWORDS_ACCEPT_INJECTED=1).
    /// Only for automated testing: normally synthetic input (macros, remote tools) is ignored.
    /// </summary>
    public static bool AcceptInjected { get; } = Environment.GetEnvironmentVariable("CHECKWORDS_ACCEPT_INJECTED") == "1";

    /// <summary>Return true to swallow the key.</summary>
    public Func<KeyInfo, bool>? KeyDown { get; set; }
    public Func<KeyInfo, bool>? KeyUp { get; set; }
    public Action? MouseDown { get; set; }

    public void Install()
    {
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook failed");
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse hook failed");
    }

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & Native.LLKHF_INJECTED) != 0;
                bool ours = data.dwExtraInfo == InputSender.Signature;
                if (!ours && (!injected || AcceptInjected))
                {
                    int msg = (int)wParam;
                    var key = new KeyInfo((int)data.vkCode, (int)data.scanCode, data.flags, data.time);
                    bool swallow = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN
                        ? KeyDown?.Invoke(key) == true
                        : msg is Native.WM_KEYUP or Native.WM_SYSKEYUP && KeyUp?.Invoke(key) == true;
                    if (swallow) return 1;
                }
            }
        }
        catch
        {
            // Never let an exception escape into the hook chain: it would kill keyboard input handling.
        }
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN
                    or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
                MouseDown?.Invoke();
        }
        catch
        {
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
