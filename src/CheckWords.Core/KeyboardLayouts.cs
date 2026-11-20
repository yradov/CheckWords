namespace CheckWords.Core;

/// <summary>Turns physical key strokes into text for a given language layout and back.</summary>
public interface IKeyMapper
{
    IReadOnlyList<Lang> Langs { get; }
    string? Map(KeyStroke key, Lang lang);
    KeyStroke? FromChar(char c, Lang lang);
}

public sealed record LayoutInfo(IntPtr Hkl, Lang Lang)
{
    public override string ToString() => $"{Lang.Code()} (HKL {Hkl.ToInt64() & 0xFFFFFFFF:X8})";
}

/// <summary>
/// The installed Windows layouts that we know how to work with (EN / RU / UK).
/// A layout is recognised by what it types on the S key, not by its locale id:
/// e.g. "Russian" can be installed under the ru-UA locale (0x2000), which has no primary language.
/// </summary>
public sealed class KeyboardLayouts : IKeyMapper
{
    readonly List<LayoutInfo> _layouts;
    readonly Dictionary<IntPtr, Lang> _hklLang = new();
    readonly Dictionary<(int Vk, bool Shift, bool Caps, bool AltGr, Lang Lang), string?> _cache = new();

    KeyboardLayouts(List<LayoutInfo> layouts)
    {
        _layouts = layouts;
        Langs = layouts.Select(l => l.Lang).ToList();
        foreach (var l in layouts) _hklLang[l.Hkl] = l.Lang;
    }

    public IReadOnlyList<LayoutInfo> Layouts => _layouts;
    public IReadOnlyList<Lang> Langs { get; }

    public static KeyboardLayouts Detect()
    {
        int n = Native.GetKeyboardLayoutList(0, null);
        var handles = new IntPtr[Math.Max(n, 0)];
        if (n > 0) Native.GetKeyboardLayoutList(n, handles);

        var list = new List<LayoutInfo>();
        foreach (var hkl in handles)
        {
            var lang = Classify(hkl);
            if (lang != Lang.Unknown && list.All(l => l.Lang != lang))
                list.Add(new LayoutInfo(hkl, lang));
        }
        list.Sort((a, b) => a.Lang.CompareTo(b.Lang));
        return new KeyboardLayouts(list);
    }

    public static Lang Classify(IntPtr hkl)
    {
        var s = MapRaw(new KeyStroke(Vk.S, 0, false, false), hkl);
        var q = MapRaw(new KeyStroke(Vk.Q, 0, false, false), hkl);
        var z = MapRaw(new KeyStroke(Vk.Z, 0, false, false), hkl);
        return s switch
        {
            "s" when q == "q" && z == "z" => Lang.En,
            "ы" => Lang.Ru,
            "і" => Lang.Uk,
            _ => Lang.Unknown,
        };
    }

    public LayoutInfo? Get(Lang lang) => _layouts.FirstOrDefault(l => l.Lang == lang);

    public Lang LangOf(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return Lang.Unknown;
        if (_hklLang.TryGetValue(hkl, out var lang)) return lang;
        lang = Classify(hkl);
        if (lang != Lang.Unknown && Get(lang) is null) lang = Lang.Unknown; // a layout we did not load
        _hklLang[hkl] = lang;
        return lang;
    }

    public string? Map(KeyStroke key, Lang lang)
    {
        var cacheKey = (key.Vk, key.Shift, key.Caps, key.AltGr, lang);
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        var info = Get(lang);
        var result = info is null ? null : MapRaw(key, info.Hkl);
        _cache[cacheKey] = result;
        return result;
    }

    public KeyStroke? FromChar(char c, Lang lang)
    {
        var info = Get(lang);
        if (info is null) return null;
        short r = Native.VkKeyScanExW(c, info.Hkl);
        if (r == -1) return null;
        int vk = r & 0xFF;
        int state = (r >> 8) & 0xFF;
        bool shift = (state & 1) != 0, ctrl = (state & 2) != 0, alt = (state & 4) != 0;
        if (ctrl != alt) return null; // Ctrl-only or Alt-only chars are control codes, not text
        return new KeyStroke(vk, 0, shift, false, ctrl && alt);
    }

    public static string? MapRaw(KeyStroke key, IntPtr hkl)
    {
        var state = new byte[256];
        if (key.Shift) { state[Vk.Shift] = 0x80; state[Vk.LShift] = 0x80; }
        if (key.Caps) state[Vk.Capital] = 0x01;
        if (key.AltGr)
        {
            state[Vk.Control] = 0x80; state[Vk.LControl] = 0x80;
            state[Vk.Menu] = 0x80; state[Vk.RMenu] = 0x80;
        }
        uint scan = key.Scan != 0 ? (uint)key.Scan : Native.MapVirtualKeyEx((uint)key.Vk, 0, hkl);
        var buf = new char[8];
        int n = Native.ToUnicodeEx((uint)key.Vk, scan, state, buf, buf.Length, Native.TOUNICODE_NO_STATE_CHANGE, hkl);
        if (n <= 0) return null; // no character, or a dead key
        var s = new string(buf, 0, Math.Min(n, buf.Length));
        return s.Any(char.IsControl) ? null : s;
    }
}
