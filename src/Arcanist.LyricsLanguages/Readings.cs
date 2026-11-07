using System.Text;
using Arcanist.Extensibility;
using ICU4N.Text;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// How lyrics are pronounced, for players: Japanese in romaji with furigana (MeCab and IPADIC), Chinese in pinyin,
/// Korean in the Revised Romanization, Cyrillic as English speakers read it, and other scripts (Cyrillic, Greek, Arabic, Hebrew, Thai, Indian scripts…) in the
/// Latin alphabet with ICU's transliterations. Lyrics in the Latin alphabet need none: only their language is said.
/// </summary>
public sealed class LyricsReadings(SettingsStore settings, Components components) : ILyricsReadings, IDisposable
{
    /// <summary>Raise when the readings would come out different, so Arcanist works them out again.</summary>
    private const int Method = 1;

    private static readonly Dictionary<string, string> TransliteratorIds = new()
    {
        ["zh"] = "Han-Latin",
        ["el"] = "Greek-Latin/UNGEGN",
        ["fa"] = "Persian-Latin/BGN",
        ["ka"] = "Georgian-Latin/BGN",
        ["hy"] = "Armenian-Latin/BGN",
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Transliterator> _transliterators = new();
    private Japanese? _japanese;

    private Japanese JapaneseReader
    {
        get
        {
            lock (_gate)
                return _japanese ??= new Japanese(components.DictionaryFolder);
        }
    }

    public string Version
    {
        get
        {
            var current = settings.Current;
            // What the readings depend on: the method, furigana on or off, and whether the Japanese dictionary is here.
            return $"{Method}.{(current.Furigana ? 1 : 0)}.{(components.IsInstalled(Components.Dictionary) ? 1 : 0)}";
        }
    }

    public string? LanguageOf(IReadOnlyList<string> lines) => settings.Current.Readings ? Scripts.LanguageOf(lines) : null;

    public IReadOnlyList<LyricsReading?> Read(IReadOnlyList<string> lines, string language)
    {
        var current = settings.Current;
        var script = Scripts.ScriptOf(language);
        if (!current.Readings || script == Script.Latin)
            return lines.Select(_ => (LyricsReading?)null).ToList();
        if (script == Script.Kana && !(components.IsInstalled(Components.Dictionary) && JapaneseReader.Ready))
            return lines.Select(_ => (LyricsReading?)null).ToList();

        return lines.Select(line => Read(line, language, script, current.Furigana)).ToList();
    }

    private LyricsReading? Read(string line, string language, Script script, bool furigana)
    {
        if (!Scripts.NeedsReading(line))
            return null;
        return script switch
        {
            Script.Kana => JapaneseReader.Read(line, furigana),
            Script.Hangul => new LyricsReading(Tidy(Korean.Romanize(line)), null),
            Script.Cyrillic => new LyricsReading(Tidy(Cyrillic.Romanize(line, language)), null),
            _ => new LyricsReading(Tidy(Transliterate(line, language)), null),
        };
    }

    /// <summary>The line in the Latin alphabet with ICU (its own transliteration for the language, or any script's).</summary>
    public string Transliterate(string line, string language)
    {
        Transliterator transliterator;
        lock (_gate)
        {
            if (!_transliterators.TryGetValue(language, out transliterator!))
            {
                transliterator = Create(TransliteratorIds.GetValueOrDefault(language, "Any-Latin"));
                _transliterators[language] = transliterator;
            }

            // A transliterator is not safe to use from several threads at once.
            return transliterator.Transliterate(line.Normalize(NormalizationForm.FormKC));
        }
    }

    private static Transliterator Create(string id)
    {
        try
        {
            return Transliterator.GetInstance(id);
        }
        catch (ArgumentException)
        {
            return Transliterator.GetInstance("Any-Latin");
        }
    }

    /// <summary>One space between words, none before punctuation.</summary>
    private static string Tidy(string text)
    {
        var spaced = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var mark in new[] { ",", ".", "!", "?", ";", ":" })
            spaced = spaced.Replace(" " + mark, mark);
        return spaced.Trim();
    }

    public void Dispose()
    {
        lock (_gate)
            _japanese?.Dispose();
    }
}
