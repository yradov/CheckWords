namespace CheckWords.Core;

public enum Lang
{
    Unknown = 0,
    En,
    Ru,
    Uk,
}

public static class LangExtensions
{
    public static string Code(this Lang lang) => lang switch
    {
        Lang.En => "EN",
        Lang.Ru => "RU",
        Lang.Uk => "UK",
        _ => "??",
    };

    public static bool IsCyrillic(this Lang lang) => lang is Lang.Ru or Lang.Uk;
}

/// <summary>
/// A physical key press, stored independently of the layout. The same stroke can be read
/// in any layout, which is what makes "retype in the other layout" possible.
/// </summary>
public readonly record struct KeyStroke(int Vk, int Scan, bool Shift, bool Caps, bool AltGr = false);

public static class Vk
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int Pause = 0x13;
    public const int Capital = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int Prior = 0x21;
    public const int Down = 0x28;
    public const int Snapshot = 0x2C;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int C = 0x43;
    public const int Q = 0x51;
    public const int S = 0x53;
    public const int Z = 0x5A;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int Apps = 0x5D;
    public const int F1 = 0x70;
    public const int F24 = 0x87;
    public const int NumLock = 0x90;
    public const int Scroll = 0x91;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;

    /// <summary>Keys that never change the text by themselves.</summary>
    public static bool IsModifier(int vk) => vk is Shift or Control or Menu or LShift or RShift
        or LControl or RControl or LMenu or RMenu or LWin or RWin or Capital or NumLock or Scroll;

    /// <summary>Keys that move the caret or focus: after them the buffer no longer matches the text.</summary>
    public static bool IsResetKey(int vk) => vk is Tab or Escape or Insert or Delete or Apps or Pause or Snapshot
        or (>= Prior and <= Down) or (>= F1 and <= F24) or (>= 0xA6 and <= 0xB7);
}
