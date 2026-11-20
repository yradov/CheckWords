namespace CheckWords.Core;

/// <summary>Loads the embedded word lists (Data/*.txt) and builds the language models.</summary>
public static class LanguageData
{
    public static IEnumerable<string> Embedded(Lang lang)
    {
        var name = $"CheckWords.Data.{lang.ToString().ToLowerInvariant()}.txt";
        using var stream = typeof(LanguageData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded word list '{name}' not found.");
        using var reader = new StreamReader(stream);
        return Tokenize(reader.ReadToEnd()).ToList();
    }

    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                foreach (var part in token.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Any(char.IsLetter)) yield return part;
        }
    }

    /// <param name="extraDictionaryDir">
    /// Optional folder with big word lists, one word per line: en*.txt, ru*.txt, uk*.txt
    /// (e.g. exported from Hunspell). They extend both the dictionary and the trigram model.
    /// </param>
    /// <param name="userWords">Words the user marked as "never warn"; added to the dictionary only.</param>
    public static Dictionary<Lang, LanguageModel> Build(IEnumerable<Lang> langs, IEnumerable<string>? userWords = null,
        string? extraDictionaryDir = null)
    {
        var models = langs.Distinct().ToDictionary(l => l, l => new LanguageModel(l, Embedded(l)));

        if (extraDictionaryDir is not null && Directory.Exists(extraDictionaryDir))
        {
            foreach (var (lang, model) in models)
            {
                var pattern = lang.ToString().ToLowerInvariant() + "*.txt";
                foreach (var file in Directory.EnumerateFiles(extraDictionaryDir, pattern))
                    foreach (var word in Tokenize(File.ReadAllText(file)))
                        model.AddWord(word, train: true);
            }
        }

        if (userWords is null) return models;

        foreach (var word in userWords)
        {
            var w = LanguageModel.Normalize(word.Trim());
            if (w.Length == 0) continue;
            foreach (var lang in GuessLangs(w))
                if (models.TryGetValue(lang, out var m)) m.AddWord(w, train: false);
        }
        return models;
    }

    /// <summary>Which languages a user-dictionary word may belong to, judged by its letters.</summary>
    public static IEnumerable<Lang> GuessLangs(string w)
    {
        bool latin = w.Any(c => c is >= 'a' and <= 'z');
        bool cyr = w.Any(c => c is >= 'Ѐ' and <= 'ӿ');
        if (latin && !cyr) { yield return Lang.En; yield break; }
        if (!cyr) yield break;
        bool ruOnly = w.Any(c => c is 'ы' or 'э' or 'ъ' or 'ё');
        bool ukOnly = w.Any(c => c is 'і' or 'ї' or 'є' or 'ґ');
        if (!ukOnly) yield return Lang.Ru;
        if (!ruOnly) yield return Lang.Uk;
    }
}
