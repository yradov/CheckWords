using System.Text;

namespace CheckWords.Core;

/// <summary>
/// Loads the word lists and builds the language models: the embedded lists for EN / RU / UK
/// (Data/*.txt) plus the user's dictionaries for any language (dict\{code}*.txt or Hunspell *.dic).
/// </summary>
public static class LanguageData
{
    static LanguageData() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // Hunspell's cp1251 etc.

    /// <summary>The word list built into the program, or nothing for a language without one.</summary>
    public static IEnumerable<string> Embedded(Lang lang)
    {
        var name = $"CheckWords.Data.{lang.Id}.txt";
        using var stream = typeof(LanguageData).Assembly.GetManifestResourceStream(name);
        if (stream is null) return Array.Empty<string>();
        using var reader = new StreamReader(stream);
        return Tokenize(reader.ReadToEnd()).ToList();
    }

    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            foreach (var rawToken in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                // Hunspell .dic: "word/AFFIXFLAGS", the flags are not part of the word.
                int slash = rawToken.IndexOf('/');
                var token = slash >= 0 ? rawToken[..slash] : rawToken;
                foreach (var part in token.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Any(char.IsLetter)) yield return part;
            }
        }
    }

    /// <summary>The user's dictionary files for a language: {code}*.txt and {code}*.dic (en_US.dic, bg_BG.dic…).</summary>
    public static IEnumerable<string> DictionaryFiles(string dir, Lang lang)
    {
        if (lang == Lang.Unknown || !Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, lang.Id + "*.txt")
            .Concat(Directory.EnumerateFiles(dir, lang.Id + "*.dic"))
            .Where(f => IsFileOf(f, lang))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"en.txt", "en_US.dic", "en-big.txt" belong to EN; "eng.txt" does not.</summary>
    static bool IsFileOf(string path, Lang lang)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == lang.Id.Length || !char.IsLetter(name[lang.Id.Length]);
    }

    /// <summary>
    /// Reads a word list. Hunspell dictionaries name their encoding in the .aff file next to them
    /// ("SET microsoft-cp1251"); everything else is read as UTF-8.
    /// </summary>
    public static string ReadDictionary(string path)
    {
        var encoding = Encoding.UTF8;
        var aff = Path.ChangeExtension(path, ".aff");
        if (path.EndsWith(".dic", StringComparison.OrdinalIgnoreCase) && File.Exists(aff))
        {
            foreach (var line in File.ReadLines(aff, Encoding.Latin1))
            {
                if (!line.StartsWith("SET ", StringComparison.Ordinal)) continue;
                var name = line[4..].Trim().Replace("microsoft-cp", "windows-", StringComparison.OrdinalIgnoreCase);
                try { encoding = Encoding.GetEncoding(name); } catch (ArgumentException) { }
                break;
            }
        }
        return File.ReadAllText(path, encoding);
    }

    /// <summary>Builds a model for every language that has words: built-in, or from the user's dictionaries.</summary>
    /// <param name="extraDictionaryDir">
    /// Optional folder with word lists, one word per line: en*.txt, bg*.txt, bg_BG.dic…
    /// They extend both the dictionary and the trigram model; for a language without a built-in list
    /// they are the only source.
    /// </param>
    /// <param name="userWords">Words the user marked as "never warn"; added to the dictionary only.</param>
    /// <param name="letters">
    /// The letters each layout can type, to decide which languages a user word belongs to.
    /// Without it, the letters seen in each language's word lists are used.
    /// </param>
    public static Dictionary<Lang, LanguageModel> Build(IEnumerable<Lang> langs, IEnumerable<string>? userWords = null,
        string? extraDictionaryDir = null, Func<Lang, IReadOnlySet<char>>? letters = null)
    {
        var models = new Dictionary<Lang, LanguageModel>();
        foreach (var lang in langs.Distinct())
        {
            var words = Embedded(lang).ToList();
            if (extraDictionaryDir is not null)
                foreach (var file in DictionaryFiles(extraDictionaryDir, lang))
                    words.AddRange(Tokenize(ReadDictionary(file)));
            if (words.Count > 0) models[lang] = new LanguageModel(lang, words);
        }

        if (userWords is null) return models;

        foreach (var word in userWords)
        {
            var w = LanguageModel.Normalize(word.Trim());
            if (w.Length == 0) continue;
            foreach (var (lang, model) in models)
                if (CanSpell(w, letters?.Invoke(lang) ?? model.Alphabet))
                    model.AddWord(w, train: false);
        }
        return models;
    }

    /// <summary>True when every letter of the word is in the set: "ы" can't be UK, "і" can't be RU.</summary>
    public static bool CanSpell(string normalizedWord, IReadOnlySet<char> letters) =>
        normalizedWord.Any(char.IsLetter) && normalizedWord.All(c => !char.IsLetter(c) || letters.Contains(c));
}
