namespace CheckWords.Core;

/// <summary>What to send to the window to retype the last word in another layout.</summary>
public sealed record ConversionPlan(int Start, int End, int Trailing, Lang From, Lang To, int DeleteCount, string Insert);

/// <summary>
/// Remembers what was typed into the current window since the caret last moved, as physical key
/// strokes. Raises <see cref="Warning"/> when a word looks like it was typed in the wrong layout and
/// plans the conversion of the last word on the hotkey.
/// </summary>
public sealed class TypingSession
{
    public readonly record struct Entry(KeyStroke Key, Lang Lang, bool IsWord);

    const int MaxEntries = 256;
    const int MaxTrailingSpaces = 4;

    readonly List<Entry> _entries = new();
    readonly IKeyMapper _map;
    readonly LayoutDetector _detector;
    int _warnedStart = -1;
    int _version;
    (int Start, int End, int Version)? _lastConversion;

    public TypingSession(IKeyMapper map, LayoutDetector detector)
    {
        _map = map;
        _detector = detector;
    }

    public bool WarnMidWord { get; set; } = true;
    public event Action<Suggestion>? Warning;
    public IReadOnlyList<Entry> Entries => _entries;
    public Lang PreferredCyrillic => _detector.PreferredCyrillic;

    public void Reset()
    {
        _entries.Clear();
        _warnedStart = -1;
        _lastConversion = null;
        _version++;
    }

    /// <param name="lang">Layout of the focused window at the moment of the key press.</param>
    /// <param name="ctrl">Ctrl held (not as part of AltGr).</param>
    /// <param name="alt">Alt held (not as part of AltGr).</param>
    public void OnKey(KeyStroke key, Lang lang, bool ctrl = false, bool alt = false, bool win = false)
    {
        if (Vk.IsModifier(key.Vk)) return;
        _version++;
        if (ctrl || alt || win) { Reset(); return; } // a shortcut: we no longer know where the caret is

        switch (key.Vk)
        {
            case Vk.Back:
                if (_entries.Count > 0) _entries.RemoveAt(_entries.Count - 1);
                if (CurrentWord() is null) _warnedStart = -1;
                return;
            case Vk.Space:
                CheckWord(complete: true);
                Add(new Entry(key, lang, false));
                return;
            case Vk.Return:
                CheckWord(complete: true);
                Reset();
                return;
        }

        if (Vk.IsResetKey(key.Vk) || lang == Lang.Unknown) { Reset(); return; }
        var ch = _map.Map(key, lang);
        if (string.IsNullOrEmpty(ch)) { Reset(); return; }

        // A key belongs to a word if it types a letter in ANY layout: "," on EN is "б" on RU.
        bool isWord = _map.Langs.Any(l => IsWordText(_map.Map(key, l)));
        if (isWord)
        {
            Add(new Entry(key, lang, true));
            if (WarnMidWord) CheckWord(complete: false);
        }
        else
        {
            CheckWord(complete: true);
            Add(new Entry(key, lang, false));
        }
    }

    /// <summary>Plans retyping the last word (and the spaces after it) in another layout. Pressing again cycles.</summary>
    public ConversionPlan? PlanWordConversion()
    {
        int i = _entries.Count - 1, trailing = 0;
        while (i >= 0 && !_entries[i].IsWord && _entries[i].Key.Vk == Vk.Space && trailing < MaxTrailingSpaces)
        {
            trailing++;
            i--;
        }
        if (i < 0 || !_entries[i].IsWord) return null;
        int end = i;
        while (i >= 0 && _entries[i].IsWord) i--;
        int start = i + 1;

        var keys = KeysOf(start, end);
        var from = _entries[end].Lang;
        int deleteCount = trailing;
        for (int k = start; k <= end; k++)
            deleteCount += (_map.Map(_entries[k].Key, _entries[k].Lang) ?? "").Length;

        bool repeat = _lastConversion is { } lc && lc.Start == start && lc.End == end && lc.Version == _version;
        var to = repeat
            ? _detector.NextInCycle(keys, from)
            : _detector.BestAlternative(keys, from, complete: trailing > 0);
        if (to == Lang.Unknown) return null;

        var text = _detector.TextFor(keys, to);
        if (text is null) return null;
        return new ConversionPlan(start, end, trailing, from, to, deleteCount, text + new string(' ', trailing));
    }

    /// <summary>Call after the plan was typed into the window.</summary>
    public void Apply(ConversionPlan plan)
    {
        for (int k = plan.Start; k <= plan.End + plan.Trailing && k < _entries.Count; k++)
            _entries[k] = _entries[k] with { Lang = plan.To };
        _warnedStart = plan.Start;
        _lastConversion = (plan.Start, plan.End, _version);
        if (plan.To.IsCyrillic()) _detector.PreferredCyrillic = plan.To;
    }

    void CheckWord(bool complete)
    {
        if (CurrentWord() is not var (start, end)) return;
        if (_warnedStart == start) return;

        var lang = _entries[end].Lang;
        var suggestion = _detector.Analyze(KeysOf(start, end), lang, complete);
        if (suggestion is not null)
        {
            _warnedStart = start;
            Warning?.Invoke(suggestion);
        }
        else if (complete && lang.IsCyrillic() && end - start + 1 >= 3)
        {
            _detector.PreferredCyrillic = lang;
        }
    }

    (int Start, int End)? CurrentWord()
    {
        if (_entries.Count == 0 || !_entries[^1].IsWord) return null;
        int end = _entries.Count - 1, i = end;
        while (i >= 0 && _entries[i].IsWord) i--;
        return (i + 1, end);
    }

    List<KeyStroke> KeysOf(int start, int end)
    {
        var keys = new List<KeyStroke>(end - start + 1);
        for (int k = start; k <= end; k++) keys.Add(_entries[k].Key);
        return keys;
    }

    void Add(Entry entry)
    {
        _entries.Add(entry);
        if (_entries.Count <= MaxEntries) return;
        _entries.RemoveRange(0, MaxEntries / 4);
        _warnedStart = -1;
        _lastConversion = null;
    }

    static bool IsWordText(string? s) => s is { Length: > 0 } && (char.IsLetter(s[0]) || s[0] is '\'' or 'ʼ');
}
