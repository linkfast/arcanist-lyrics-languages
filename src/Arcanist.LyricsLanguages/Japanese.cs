using System.Text;
using Arcanist.Extensibility;
using MeCab;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Japanese lyrics in romaji and with furigana: MeCab splits each line into words with their reading (IPADIC), the
/// readings become Hepburn romaji (with は, へ and を as particles read wa, e and o), and the words with kanji get their
/// reading in hiragana above them, without repeating the kana they already show.
/// </summary>
public sealed class Japanese(string dictionaryFolder) : IDisposable
{
    private readonly Lock _gate = new();
    private MeCabTagger? _tagger;

    public bool Ready => File.Exists(Path.Combine(dictionaryFolder, "sys.dic"));

    /// <summary>A word of a line: as written, its reading in hiragana (null when unknown), and what it is.</summary>
    public sealed record Word(string Surface, string? Reading, string PartOfSpeech, string Detail);

    public IReadOnlyList<Word> Words(string line)
    {
        lock (_gate)
        {
            _tagger ??= MeCabTagger.Create(new MeCabParam { DicDir = dictionaryFolder });
            var words = new List<Word>();
            foreach (var node in _tagger.ParseToNodes(line))
            {
                if (node.CharType <= 0 || string.IsNullOrEmpty(node.Surface))
                    continue;
                var feature = (node.Feature ?? "").Split(',');
                var reading = feature.Length > 7 && feature[7] != "*" ? Hiragana(feature[7]) : null;
                words.Add(new Word(node.Surface, reading, feature[0], feature.Length > 1 ? feature[1] : ""));
            }

            return words;
        }
    }

    public LyricsReading? Read(string line, bool furigana)
    {
        if (!Scripts.NeedsReading(line))
            return null;
        var words = Words(line);
        return new LyricsReading(Romaji(words), furigana ? Ruby(words) : null);
    }

    // —— Romaji ——

    /// <summary>The line in romaji: words apart, what attaches to a word (endings, auxiliaries, て) attached to it.</summary>
    public static string Romaji(IReadOnlyList<Word> words)
    {
        // Each group (a word and what attaches to it) is turned into romaji as a whole, so a small つ at the end of one
        // part doubles the consonant of the next (待っ + て: matte).
        var parts = new List<string>();
        var kana = new StringBuilder();
        void Flush()
        {
            if (kana.Length > 0)
                parts[^1] += ToRomaji(kana.ToString());
            kana.Clear();
        }

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word.PartOfSpeech == "記号" || word.Surface.All(c => !char.IsLetterOrDigit(c)))
            {
                Flush();
                var mark = Punctuation(word.Surface);
                if (parts.Count == 0)
                    parts.Add(mark);
                else
                    parts[^1] += mark;
                continue;
            }

            // Endings and auxiliaries go with the word before them ("yonda", "mite").
            var attaches = parts.Count > 0 && (word.PartOfSpeech is "助動詞" || word.PartOfSpeech == "助詞" && word.Detail == "接続助詞"
                                               || word.Detail is "接尾" || word.PartOfSpeech == "動詞" && word.Detail == "接尾");
            if (!attaches)
            {
                Flush();
                parts.Add("");
            }

            if (word.PartOfSpeech == "助詞" && word.Surface is "は" or "へ" or "を")
            {
                Flush();
                parts[^1] += word.Surface switch { "は" => "wa", "へ" => "e", _ => "o" };
            }
            else if (word.Reading is { } reading && word.Surface.Any(c => Scripts.Of(c) is Script.Kana or Script.Han))
                kana.Append(reading);
            else
            {
                Flush();
                parts[^1] += word.Surface;
            }
        }

        Flush();
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }
    private static string Punctuation(string text) => text switch
    {
        "、" or "，" => ",",
        "。" or "．" => ".",
        "！" => "!",
        "？" => "?",
        "「" or "」" or "『" or "』" => "\"",
        "・" => " ",
        "〜" or "～" => "~",
        _ => text.Normalize(NormalizationForm.FormKC),
    };

    /// <summary>Hiragana (and katakana) in Hepburn romaji.</summary>
    public static string ToRomaji(string kana)
    {
        var text = Hiragana(kana);
        var output = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // A small つ doubles the consonant after it (っち: tchi).
            if (c == 'っ')
            {
                var next = i + 1 < text.Length ? Syllable(text, i + 1).Romaji : "";
                if (next.Length > 0 && !"aeiou".Contains(next[0]))
                    output.Append(next.StartsWith("ch", StringComparison.Ordinal) ? 't' : next[0]);
                continue;
            }

            // A long mark repeats the vowel before it.
            if (c == 'ー')
            {
                var last = output.Length > 0 ? output[^1] : ' ';
                if ("aeiou".Contains(last))
                    output.Append(last);
                continue;
            }

            var (romaji, length) = Syllable(text, i);
            // ん before a vowel or y is n' (kan'i), so it is not read as na, ni…
            if (c == 'ん' && i + 1 < text.Length && Syllable(text, i + 1).Romaji is { Length: > 0 } after && "aeiouy".Contains(after[0]))
                romaji = "n'";
            output.Append(romaji);
            i += length - 1;
        }

        return output.ToString();
    }

    private static (string Romaji, int Length) Syllable(string text, int at)
    {
        if (at + 1 < text.Length && Digraphs.TryGetValue(text.Substring(at, 2), out var two))
            return (two, 2);
        return Monographs.TryGetValue(text[at], out var one) ? (one, 1) : (text[at].ToString(), 1);
    }

    /// <summary>Katakana as hiragana (other characters as they are).</summary>
    public static string Hiragana(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            builder.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return builder.ToString();
    }

    // —— Furigana ——

    /// <summary>The line in parts: each word with kanji has its reading above it, without the kana it shows itself.</summary>
    public static IReadOnlyList<RubySegment>? Ruby(IReadOnlyList<Word> words)
    {
        if (!words.Any(w => w.Surface.Any(c => Scripts.Of(c) == Script.Han)))
            return null;
        var segments = new List<RubySegment>();
        void Plain(string text)
        {
            if (text.Length == 0)
                return;
            if (segments.Count > 0 && segments[^1].Reading is null)
                segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            else
                segments.Add(new RubySegment(text, null));
        }

        foreach (var word in words)
        {
            if (word.Reading is not { } reading || !word.Surface.Any(c => Scripts.Of(c) == Script.Han))
            {
                Plain(word.Surface);
                continue;
            }

            // The kana at both ends of the word are read as written: only what is between needs its reading.
            var surface = word.Surface;
            var head = 0;
            while (head < surface.Length && head < reading.Length && Scripts.Of(surface[head]) == Script.Kana && Hiragana(surface[head].ToString())[0] == reading[head])
                head++;
            var tail = 0;
            while (tail < surface.Length - head && tail < reading.Length - head && Scripts.Of(surface[^(tail + 1)]) == Script.Kana
                   && Hiragana(surface[^(tail + 1)].ToString())[0] == reading[^(tail + 1)])
                tail++;
            Plain(surface[..head]);
            segments.Add(new RubySegment(surface[head..(surface.Length - tail)], reading[head..(reading.Length - tail)]));
            Plain(surface[(surface.Length - tail)..]);
        }

        return segments;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tagger?.Dispose();
            _tagger = null;
        }
    }

    // —— Hepburn ——

    private static readonly Dictionary<char, string> Monographs = new()
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "i", ['ゑ'] = "e", ['を'] = "o", ['ん'] = "n",
        ['ゔ'] = "vu", ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo", ['ゎ'] = "wa",
    };

    private static readonly Dictionary<string, string> Digraphs = BuildDigraphs();

    private static Dictionary<string, string> BuildDigraphs()
    {
        var digraphs = new Dictionary<string, string>();
        var rows = new Dictionary<char, string>
        {
            ['き'] = "ky", ['ぎ'] = "gy", ['し'] = "sh", ['じ'] = "j", ['ち'] = "ch", ['ぢ'] = "j", ['に'] = "ny", ['ひ'] = "hy",
            ['び'] = "by", ['ぴ'] = "py", ['み'] = "my", ['り'] = "ry",
        };
        foreach (var (kana, start) in rows)
        {
            digraphs[$"{kana}ゃ"] = start + "a";
            digraphs[$"{kana}ゅ"] = start + "u";
            digraphs[$"{kana}ょ"] = start + "o";
            digraphs[$"{kana}ぇ"] = start + "e";
        }

        // Sounds written with small vowels, mostly in loanwords.
        foreach (var (pair, romaji) in new Dictionary<string, string>
                 {
                     ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo", ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du",
                     ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo", ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo",
                     ["つぁ"] = "tsa", ["つぃ"] = "tsi", ["つぇ"] = "tse", ["つぉ"] = "tso", ["ちぇ"] = "che", ["しぇ"] = "she", ["じぇ"] = "je",
                 })
            digraphs[pair] = romaji;
        return digraphs;
    }
}
