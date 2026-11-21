using System.Text.RegularExpressions;

namespace CheckWords.Core;

/// <summary>"This word looks like it was meant for layout <see cref="To"/>."</summary>
public sealed record Suggestion(Lang From, Lang To, string FromText, string ToText, bool Known, double FromScore, double ToScore);

public sealed class DetectorOptions
{
    /// <summary>Minimum letters before a finished word is checked at all.</summary>
    public int MinLetters { get; set; } = 2;
    /// <summary>Minimum letters before an unfinished word is checked.</summary>
    public int MidMinLetters { get; set; } = 3;
    /// <summary>Statistics-only decisions (no dictionary hit) need at least this many letters.</summary>
    public int StatMinLetters { get; set; } = 4;
    public int MidStatMinLetters { get; set; } = 4;
    /// <summary>How much better (per char, log-prob) the alternative must score.</summary>
    public double StatMargin { get; set; } = 1.0;
    public double MidStatMargin { get; set; } = 1.8;
    /// <summary>The current text must score at most this to be considered gibberish.</summary>
    public double StatCurMax { get; set; } = -3.4;
    /// <summary>The alternative must score at least this to be considered a plausible word.</summary>
    public double StatAltMin { get; set; } = -3.6;
    /// <summary>Same, when the current text has punctuation inside a word.</summary>
    public double JunkAltMin { get; set; } = -4.2;
    /// <summary>A dictionary word may score up to this much worse than the current text and still win.</summary>
    public double KnownTolerance { get; set; } = 1.5;
    /// <summary>Two layouts of the same script (RU ↔ UK, RU ↔ BG): required total (not per-char) log-prob advantage.</summary>
    public double CyrSumMargin { get; set; } = 4.0;
}

/// <summary>Decides whether a sequence of key strokes was typed in the wrong layout.</summary>
public sealed class LayoutDetector
{
    static readonly Regex WordRx = new(@"[\p{L}']+", RegexOptions.Compiled);

    readonly IKeyMapper _map;
    readonly IReadOnlyDictionary<Lang, LanguageModel> _models;
    readonly List<Lang> _langs;
    readonly Dictionary<Script, Lang> _preferred = new();

    public LayoutDetector(IKeyMapper map, IReadOnlyDictionary<Lang, LanguageModel> models, DetectorOptions? options = null)
    {
        _map = map;
        _models = models;
        _langs = map.Langs.Where(models.ContainsKey).ToList();
        Options = options ?? new DetectorOptions();
    }

    public DetectorOptions Options { get; }
    public IReadOnlyList<Lang> Langs => _langs;

    /// <summary>
    /// Layouts of one script type the same letters on many keys (RU and UK on most of them); when a
    /// conversion gives the same text in both, the layout the user used last for that script wins.
    /// By default it is the first one in sort order (RU before UK).
    /// </summary>
    public Lang PreferredFor(Script script)
    {
        if (script == Script.Other) return Lang.Unknown;
        if (_preferred.TryGetValue(script, out var lang) && _langs.Contains(lang)) return lang;
        return _langs.Where(l => l.Script == script).Order().FirstOrDefault() ?? Lang.Unknown;
    }

    /// <summary>Remembers <paramref name="lang"/> as the preferred layout of its script.</summary>
    public void Prefer(Lang lang)
    {
        if (lang.Script != Script.Other && _langs.Contains(lang)) _preferred[lang.Script] = lang;
    }

    /// <summary>The remembered preferences, to carry them over to a new detector after a reload.</summary>
    public IReadOnlyCollection<Lang> Preferences => _preferred.Values;

    /// <summary>
    /// Grouped by script, scripts in sort order of their first language (EN, then the Cyrillic ones…);
    /// inside a script the preferred layout comes first.
    /// </summary>
    public List<Lang> CycleOrder()
    {
        var order = new List<Lang>();
        foreach (var lang in _langs.Order())
        {
            if (order.Contains(lang)) continue;
            if (lang.Script == Script.Other) { order.Add(lang); continue; }
            var pref = PreferredFor(lang.Script);
            order.Add(pref);
            order.AddRange(_langs.Where(l => l.Script == lang.Script && l != pref).Order());
        }
        return order;
    }

    static bool SameScript(Lang a, Lang b) => a.Script == b.Script && a.Script != Script.Other;

    public string? TextFor(IReadOnlyList<KeyStroke> keys, Lang lang)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var s = _map.Map(keys[i], lang);
            if (s is null) return null;
            parts[i] = s;
        }
        return string.Concat(parts);
    }

    /// <summary>Returns a suggestion when the word is likely in the wrong layout, otherwise null.</summary>
    public Suggestion? Analyze(IReadOnlyList<KeyStroke> keys, Lang current, bool complete)
    {
        if (keys.Count == 0 || !_models.TryGetValue(current, out var curLm)) return null;
        var curText = TextFor(keys, current);
        if (curText is null) return null;

        var curCore = Core(curText, out bool curJunk);
        // Count keys, not current letters: ",j" is one EN letter but the two-letter UK word "бо".
        int letters = keys.Count;
        if (letters < (complete ? Options.MinLetters : Options.MidMinLetters)) return null;
        if (curCore.Length == 0) return null; // only punctuation in the current layout: "[]", ",."
        if (curJunk && LooksLikeDottedName(curCore)) return null; // file.txt, example.com
        var curNorm = LanguageModel.Normalize(curCore);
        if (!curJunk && (complete ? curLm.IsKnown(curNorm) : curLm.IsKnownPrefix(curNorm))) return null;
        double sCur = curJunk ? LanguageModel.Junk : curLm.Score(curNorm, complete);

        Suggestion? best = null;
        double bestRank = double.NegativeInfinity;
        foreach (var alt in CycleOrder())
        {
            if (alt == current) continue;
            var lm = _models[alt];
            var altText = TextFor(keys, alt);
            if (altText is null || altText == curText) continue;
            var altCore = Core(altText, out bool altJunk);
            if (altJunk || !CleanEdges(altText, altCore, complete)) continue;
            int altLetters = altCore.Count(char.IsLetter);
            if (altLetters < Options.MinLetters) continue;
            int span = altLetters;
            // Punctuation inside the current text ("e;by") is strong evidence by itself.
            double altMin = curJunk ? Options.JunkAltMin : Options.StatAltMin;

            var altNorm = LanguageModel.Normalize(altCore);
            bool altKnown = complete ? lm.IsKnown(altNorm) : lm.IsKnownPrefix(altNorm);
            double sAlt = lm.Score(altNorm, complete);

            bool ok;
            if (altKnown)
                ok = sAlt > sCur - Options.KnownTolerance
                     && (complete || curJunk || span >= Options.MidStatMinLetters);
            else if (SameScript(current, alt))
            {
                // RU and UK differ in a few letters only, so compare the total, not the per-char average.
                int n = altNorm.Length + (complete ? 1 : 0);
                ok = span >= Options.StatMinLetters && (sAlt - sCur) * n >= Options.CyrSumMargin
                     && sAlt >= altMin && (complete || span >= Options.MidStatMinLetters);
            }
            else if (complete)
                ok = span >= Options.StatMinLetters - (curJunk ? 1 : 0) && sAlt - sCur >= Options.StatMargin
                     && sCur <= Options.StatCurMax && sAlt >= altMin;
            else
                ok = span >= Options.MidStatMinLetters && sAlt - sCur >= Options.MidStatMargin
                     && sCur <= Options.StatCurMax && sAlt >= altMin;
            if (!ok) continue;

            double rank = sAlt + (altKnown ? 5 : 0) + (alt == PreferredFor(alt.Script) ? 0.25 : 0);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = new Suggestion(current, Preferred(keys, alt, altText, current), curText, altText, altKnown, sCur, sAlt);
            }
        }
        return best;
    }

    /// <summary>For an explicit conversion request: the most plausible other layout, ignoring thresholds.</summary>
    public Lang BestAlternative(IReadOnlyList<KeyStroke> keys, Lang from, bool complete)
    {
        var curText = TextFor(keys, from);
        var best = Lang.Unknown;
        double bestRank = double.NegativeInfinity;
        foreach (var alt in CycleOrder())
        {
            if (alt == from) continue;
            var text = TextFor(keys, alt);
            if (text is null || text == curText) continue;
            double rank = Rank(text, alt, complete);
            if (rank > bestRank) { bestRank = rank; best = Preferred(keys, alt, text, from); }
        }
        return best;
    }

    /// <summary>The next layout in <see cref="CycleOrder"/> that gives a different text.</summary>
    public Lang NextInCycle(IReadOnlyList<KeyStroke> keys, Lang from)
    {
        var order = CycleOrder();
        int idx = order.IndexOf(from);
        var cur = TextFor(keys, from);
        for (int k = 1; k <= order.Count; k++)
        {
            var lang = order[((idx + k) % order.Count + order.Count) % order.Count];
            if (lang == from) continue;
            var text = TextFor(keys, lang);
            if (text is not null && text != cur) return lang;
        }
        return Lang.Unknown;
    }

    /// <summary>Plausibility of a whole text (several words) in a language, per char.</summary>
    public double TextScore(string text, Lang lang)
    {
        if (!_models.TryGetValue(lang, out var lm)) return LanguageModel.Junk;
        double sum = 0;
        int len = 0;
        foreach (Match m in WordRx.Matches(text))
        {
            var w = m.Value.Trim('\'');
            if (w.Length < 2) continue;
            var n = LanguageModel.Normalize(w);
            double score = lm.Score(n, true) + (lm.IsKnown(n) ? 2 : 0);
            sum += score * w.Length;
            len += w.Length;
        }
        return len == 0 ? LanguageModel.Junk / 2 : sum / len;
    }

    /// <summary>If the preferred layout of the same script gives exactly the same text, switch to it instead.</summary>
    Lang Preferred(IReadOnlyList<KeyStroke> keys, Lang alt, string altText, Lang current)
    {
        var pref = PreferredFor(alt.Script);
        return pref != Lang.Unknown && alt != pref && pref != current && TextFor(keys, pref) == altText ? pref : alt;
    }

    double Rank(string text, Lang lang, bool complete)
    {
        if (!_models.TryGetValue(lang, out var lm)) return -100;
        var core = Core(text, out bool junk);
        if (junk || core.Length == 0) return LanguageModel.Junk;
        var n = LanguageModel.Normalize(core);
        double bonus = lm.IsKnown(n) ? 5 : !complete && lm.IsKnownPrefix(n) ? 2 : 0;
        return lm.Score(n, complete) + bonus;
    }

    /// <summary>
    /// Strips leading/trailing punctuation ("word," → "word"). <paramref name="junk"/> is set when
    /// non-letters remain inside the word (apostrophe allowed): "k.lb" is not a word in any language.
    /// </summary>
    public static string Core(string text, out bool junk)
    {
        int a = 0, b = text.Length - 1;
        while (a <= b && !char.IsLetter(text[a])) a++;
        while (b >= a && !char.IsLetter(text[b])) b--;
        junk = false;
        if (a > b) return "";
        var core = text.Substring(a, b - a + 1);
        foreach (var c in core)
            if (!char.IsLetter(c) && c is not ('\'' or '’' or 'ʼ')) { junk = true; break; }
        return core;
    }

    /// <summary>
    /// The alternative may only shed ordinary punctuation around the word ("hello," is fine,
    /// "et[" is not a candidate for "уех").
    /// </summary>
    static bool CleanEdges(string text, string core, bool complete)
    {
        if (core.Length == 0) return false;
        int start = text.IndexOf(core, StringComparison.Ordinal);
        var lead = text[..start];
        var trail = text[(start + core.Length)..];
        if (lead.Any(c => !"\"'(«“„".Contains(c))) return false;
        if (!complete && trail.Length > 0) return false;
        return trail.All(c => ".,;:!?'\")»”…".Contains(c));
    }

    static bool LooksLikeDottedName(string core)
    {
        if (!core.Contains('.')) return false;
        var parts = core.Split('.');
        return parts.All(p => p.Length >= 2 && p.All(char.IsLetter));
    }
}
