namespace CheckWords.Core;

/// <summary>The writing system of a language: languages of the same script share most letters.</summary>
public enum Script
{
    Other = 0,
    Latin,
    Cyrillic,
    Greek,
}

/// <summary>
/// A language, identified by its ISO 639-1 code ("en", "bg"). Instances are interned, so one code
/// always gives the same object and reference equality works. EN, RU and UK ship with built-in word
/// lists; any other language is picked up from an installed Windows layout plus a user dictionary.
/// </summary>
public sealed class Lang : IComparable<Lang>
{
    // Declared before the instances below: static fields initialise in this order.
    static readonly Dictionary<string, Lang> All = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();
    static readonly string[] BuiltInCodes = { "en", "ru", "uk" };

    public static readonly Lang Unknown = new("", Script.Other, -1);
    public static readonly Lang En = Get("en", Script.Latin);
    public static readonly Lang Ru = Get("ru", Script.Cyrillic);
    public static readonly Lang Uk = Get("uk", Script.Cyrillic);

    readonly int _order;

    Lang(string id, Script script, int order)
    {
        Id = id;
        Script = script;
        _order = order;
    }

    /// <summary>Lower-case ISO 639-1 code: "en", "ru", "bg". Empty for <see cref="Unknown"/>.</summary>
    public string Id { get; }

    /// <summary>The script of the language's letters; set from the layout when it is first seen.</summary>
    public Script Script { get; private set; }

    /// <summary>True for EN / RU / UK: they have word lists inside the program.</summary>
    public bool IsBuiltIn => Array.IndexOf(BuiltInCodes, Id) >= 0;

    /// <summary>The interned language for a code; <paramref name="script"/> fills in a script not known yet.</summary>
    public static Lang Get(string id, Script script = Script.Other)
    {
        if (string.IsNullOrWhiteSpace(id)) return Unknown;
        lock (Gate)
        {
            if (All.TryGetValue(id, out var lang))
            {
                if (lang.Script == Script.Other && script != Script.Other) lang.Script = script;
                return lang;
            }
            int builtIn = Array.IndexOf(BuiltInCodes, id.ToLowerInvariant());
            lang = new Lang(id.ToLowerInvariant(), script, builtIn >= 0 ? builtIn : 100);
            All[id] = lang;
            return lang;
        }
    }

    /// <summary>EN, RU, UK first (in that order), then the other languages alphabetically.</summary>
    public int CompareTo(Lang? other)
    {
        if (other is null) return 1;
        int c = _order.CompareTo(other._order);
        return c != 0 ? c : string.CompareOrdinal(Id, other.Id);
    }

    public override string ToString() => this == Unknown ? "Unknown" : Id;
}

public static class LangExtensions
{
    /// <summary>Upper-case code for the UI: "EN", "BG".</summary>
    public static string Code(this Lang lang) => lang == Lang.Unknown ? "??" : lang.Id.ToUpperInvariant();

    public static bool IsCyrillic(this Lang lang) => lang.Script == Script.Cyrillic;

    /// <summary>The script a letter belongs to.</summary>
    public static Script ScriptOf(char c) => c switch
    {
        >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 'À' and <= 'ɏ' => Script.Latin,
        >= 'Ѐ' and <= 'ӿ' => Script.Cyrillic,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        _ => Script.Other,
    };
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
