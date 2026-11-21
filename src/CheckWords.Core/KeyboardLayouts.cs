using System.Globalization;
using Microsoft.Win32;

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
/// The installed Windows layouts, one per language. A layout's language is the language of the
/// keyboard itself (its KLID, e.g. 00000402 = Bulgarian), not of the input locale it is installed
/// under: "Russian" can be installed under the ru-UA locale (0x2000), which has no primary language.
/// When the keyboard id can't be resolved, EN / RU / UK are still recognised by what the S key types.
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
        var code = KeyboardLanguage(hkl);
        if (code is not null) return Lang.Get(code, LayoutScript(hkl));

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

    const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    /// <summary>
    /// ISO 639-1 code of the keyboard's language, from the HKL's high word (the keyboard id).
    /// 0xFxxx means a layout variant (e.g. Ukrainian Enhanced, 00020422), looked up by its "Layout Id"
    /// in the registry; 0xExxx is an IME (Chinese, Japanese…), which we don't handle.
    /// </summary>
    public static string? KeyboardLanguage(IntPtr hkl)
    {
        long v = hkl.ToInt64();
        int device = (int)((v >> 16) & 0xFFFF);
        int langId;
        switch (device & 0xF000)
        {
            case 0xE000:
                return null;
            case 0xF000:
                var klid = KlidForLayoutId(device & 0x0FFF);
                if (klid is null) return null;
                langId = (int)(Convert.ToUInt32(klid, 16) & 0xFFFF);
                break;
            default:
                langId = device != 0 ? device : (int)(v & 0xFFFF);
                break;
        }
        try
        {
            var name = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName;
            return name.Length is 2 or 3 && name != "iv" ? name : null; // "iv" = invariant culture
        }
        catch (CultureNotFoundException) { return null; }
        catch (ArgumentException) { return null; }
    }

    static string? KlidForLayoutId(int layoutId)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(LayoutsKey);
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("Layout Id") is string id
                    && int.TryParse(id, NumberStyles.HexNumber, null, out var n) && n == layoutId)
                    return name;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>The script most letter keys (A–Z positions) type in this layout.</summary>
    public static Script LayoutScript(IntPtr hkl)
    {
        var counts = new Dictionary<Script, int>();
        for (int vk = 0x41; vk <= 0x5A; vk++)
        {
            var s = MapRaw(new KeyStroke(vk, 0, false, false), hkl);
            if (s is not { Length: > 0 } || !char.IsLetter(s[0])) continue;
            var script = LangExtensions.ScriptOf(s[0]);
            counts[script] = counts.GetValueOrDefault(script) + 1;
        }
        return counts.Count == 0 ? Script.Other : counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>Every letter the layout can type (with Shift, and with AltGr: ґ on the Ukrainian layout).</summary>
    public IReadOnlySet<char> Letters(Lang lang)
    {
        var set = new HashSet<char>();
        if (Get(lang) is not { } info) return set;
        for (int vk = 0x20; vk <= 0xFE; vk++)
            foreach (var (shift, altGr) in new[] { (false, false), (true, false), (false, true), (true, true) })
                if (MapRaw(new KeyStroke(vk, 0, shift, false, altGr), info.Hkl) is { } s)
                    foreach (var c in s)
                        if (char.IsLetter(c)) set.Add(char.ToLowerInvariant(c));
        return set;
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
