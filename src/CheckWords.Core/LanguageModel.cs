using System.Text;

namespace CheckWords.Core;

/// <summary>
/// Dictionary of known words plus a character trigram model trained on them.
/// The trigram model scores words that are not in the dictionary: real words in a language
/// get an average log-probability around -2..-3 per char, keyboard gibberish ("ghbdtn") far lower.
/// </summary>
public sealed class LanguageModel
{
    /// <summary>Log-probability assigned to a character that does not exist in the language.</summary>
    public const double Junk = -12.0;

    readonly HashSet<string> _words = new(StringComparer.Ordinal);
    readonly HashSet<string> _prefixes = new(StringComparer.Ordinal);
    readonly HashSet<char> _alphabet = new();
    readonly Dictionary<char, int> _uni = new();
    readonly Dictionary<char, int> _ctx1 = new();
    readonly Dictionary<string, int> _bi = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _ctx2 = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _tri = new(StringComparer.Ordinal);
    int _total;

    public LanguageModel(Lang lang, IEnumerable<string> trainingWords)
    {
        Lang = lang;
        foreach (var w in trainingWords) AddWord(w, train: true);
    }

    public Lang Lang { get; }
    public int WordCount => _words.Count;
    /// <summary>Letters seen in the training words.</summary>
    public IReadOnlySet<char> Alphabet => _alphabet;

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = char.ToLowerInvariant(ch);
            if (c is '’' or 'ʼ') c = '\'';
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Adds a word to the dictionary; with <paramref name="train"/> it also feeds the trigram model.</summary>
    public void AddWord(string word, bool train)
    {
        var w = Normalize(word);
        if (w.Length == 0 || !_words.Add(w)) return;
        for (int i = 1; i <= w.Length; i++) _prefixes.Add(w[..i]);
        if (!train) return;

        foreach (var c in w) _alphabet.Add(c);
        var s = "^^" + w + "$";
        for (int i = 2; i < s.Length; i++)
        {
            Inc(_uni, s[i]);
            _total++;
            Inc(_ctx1, s[i - 1]);
            Inc(_bi, s.Substring(i - 1, 2));
            Inc(_ctx2, s.Substring(i - 2, 2));
            Inc(_tri, s.Substring(i - 2, 3));
        }
    }

    public bool IsKnown(string normalized) => _words.Contains(normalized);
    public bool IsKnownPrefix(string normalized) => _prefixes.Contains(normalized);

    /// <summary>Average log-probability per character. <paramref name="complete"/> adds the end-of-word marker.</summary>
    public double Score(string normalized, bool complete)
    {
        var s = "^^" + normalized + (complete ? "$" : "");
        double sum = 0;
        int n = 0;
        int v = _alphabet.Count + 1;
        for (int i = 2; i < s.Length; i++)
        {
            char c = s[i];
            n++;
            if (c != '$' && !_alphabet.Contains(c)) { sum += Junk; continue; }

            double p1 = (Get(_uni, c) + 0.5) / (_total + 0.5 * v);
            int c1 = Get(_ctx1, s[i - 1]);
            double p2 = c1 > 0 ? Get(_bi, s.Substring(i - 1, 2)) / (double)c1 : 0;
            int c2 = Get(_ctx2, s.Substring(i - 2, 2));
            double p3 = c2 > 0 ? Get(_tri, s.Substring(i - 2, 3)) / (double)c2 : 0;

            double p = c2 > 0 ? 0.6 * p3 + 0.3 * p2 + 0.1 * p1
                     : c1 > 0 ? 0.75 * p2 + 0.25 * p1
                     : p1;
            sum += Math.Log(p);
        }
        return n == 0 ? 0 : sum / n;
    }

    static void Inc<TKey>(Dictionary<TKey, int> d, TKey key) where TKey : notnull =>
        d[key] = d.TryGetValue(key, out var v) ? v + 1 : 1;

    static int Get<TKey>(Dictionary<TKey, int> d, TKey key) where TKey : notnull =>
        d.TryGetValue(key, out var v) ? v : 0;
}
