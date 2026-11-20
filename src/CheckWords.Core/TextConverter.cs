using System.Text;

namespace CheckWords.Core;

public sealed record TextConversion(string Text, Lang From, Lang To);

/// <summary>
/// Converts an arbitrary (selected) text: guesses which layout it was typed in and which one was
/// meant, by trying every pair and keeping the one that makes the text most plausible.
/// </summary>
public sealed class TextConverter
{
    const double MinGain = 0.5;

    readonly IKeyMapper _map;
    readonly LayoutDetector _detector;

    public TextConverter(IKeyMapper map, LayoutDetector detector)
    {
        _map = map;
        _detector = detector;
    }

    public TextConversion? Convert(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var langs = _detector.CycleOrder();
        var options = new List<(Lang From, Lang To, string Text, double Gain)>();
        var sourceScores = new Dictionary<Lang, double>();

        foreach (var from in langs)
        {
            var keys = new KeyStroke?[input.Length];
            bool mappable = true;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                keys[i] = _map.FromChar(c, from);
                if (keys[i] is null && char.IsLetter(c)) { mappable = false; break; }
            }
            if (!mappable) continue;

            double sourceScore = _detector.TextScore(input, from);
            sourceScores[from] = sourceScore;
            foreach (var to in langs)
            {
                if (to == from) continue;
                var sb = new StringBuilder(input.Length);
                for (int i = 0; i < input.Length; i++)
                    sb.Append(keys[i] is { } k ? _map.Map(k, to) ?? input[i].ToString() : input[i].ToString());
                var text = sb.ToString();
                if (text == input) continue;
                options.Add((from, to, text, _detector.TextScore(text, to) - sourceScore));
            }
        }
        if (options.Count == 0) return null;

        var best = options.MaxBy(o => o.Gain); // first max wins: preferred Cyrillic layout on ties
        if (best.Gain >= MinGain) return new TextConversion(best.Text, best.From, best.To);

        // Nothing clearly better: treat the text as typed in its most plausible layout and cycle.
        var source = sourceScores.MaxBy(kv => kv.Value).Key;
        int idx = langs.IndexOf(source);
        for (int k = 1; k < langs.Count; k++)
        {
            var to = langs[(idx + k) % langs.Count];
            var option = options.FirstOrDefault(o => o.From == source && o.To == to);
            if (option.Text is not null) return new TextConversion(option.Text, source, to);
        }
        return null;
    }
}
