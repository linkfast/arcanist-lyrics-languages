using System.Globalization;
using System.Text;

namespace Arcanist.LyricsLanguages;

/// <summary>The writing systems lyrics come in, as far as reading them goes.</summary>
public enum Script
{
    Latin,
    Kana,
    Han,
    Hangul,
    Cyrillic,
    Greek,
    Arabic,
    Hebrew,
    Thai,
    Lao,
    Khmer,
    Myanmar,
    Devanagari,
    Bengali,
    Gurmukhi,
    Gujarati,
    Tamil,
    Telugu,
    Kannada,
    Malayalam,
    Sinhala,
    Georgian,
    Armenian,
    Other,
}

/// <summary>
/// The language of lyrics from the script they are written in (and, in the Latin alphabet, from their most common
/// words): Japanese has kana, Chinese has Han without kana, Ukrainian has і, ї, є…
/// </summary>
public static class Scripts
{
    /// <summary>A script counts when at least this share of the letters is in it (a Japanese word in English lyrics does not).</summary>
    private const double Share = 0.2;

    public static Script Of(char c) => c switch
    {
        >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ' => Script.Kana,
        >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿' or '々' => Script.Han,
        >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' => Script.Hangul,
        >= 'Ѐ' and <= 'ӿ' or >= 'Ԁ' and <= 'ԯ' => Script.Cyrillic,
        >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿' => Script.Greek,
        >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Arabic,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '฀' and <= '๿' => Script.Thai,
        >= '຀' and <= '໿' => Script.Lao,
        >= 'ក' and <= '៿' => Script.Khmer,
        >= 'က' and <= '႟' => Script.Myanmar,
        >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
        >= 'ঀ' and <= '৿' => Script.Bengali,
        >= '਀' and <= '੿' => Script.Gurmukhi,
        >= '઀' and <= '૿' => Script.Gujarati,
        >= '஀' and <= '௿' => Script.Tamil,
        >= 'ఀ' and <= '౿' => Script.Telugu,
        >= 'ಀ' and <= '೿' => Script.Kannada,
        >= 'ഀ' and <= 'ൿ' => Script.Malayalam,
        >= '඀' and <= '෿' => Script.Sinhala,
        >= 'Ⴀ' and <= 'ჿ' => Script.Georgian,
        >= '԰' and <= '֏' => Script.Armenian,
        _ when char.IsLetter(c) && c < 'ɐ' || c is >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        _ when char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark => Script.Other,
        _ => Script.Latin,
    };

    /// <summary>Whether the text has letters of a script other than Latin (a line that needs reading).</summary>
    public static bool NeedsReading(string text) => text.Any(c => Of(c) is not (Script.Latin or Script.Other));


    /// <summary>The language of the lines (ISO 639-1); null when it cannot be told.</summary>
    public static string? LanguageOf(IReadOnlyList<string> lines)
    {
        var counts = new Dictionary<Script, int>();
        var letters = 0;
        foreach (var c in lines.SelectMany(l => l))
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            var script = Of(c);
            counts[script] = counts.GetValueOrDefault(script) + 1;
        }

        if (letters == 0)
            return null;
        int Count(Script s) => counts.GetValueOrDefault(s);
        var text = string.Join('\n', lines);

        if (Count(Script.Kana) > 0 && Count(Script.Kana) + Count(Script.Han) >= letters * Share)
            return "ja";
        if (Count(Script.Hangul) >= letters * Share)
            return "ko";
        if (Count(Script.Han) >= letters * Share)
            return "zh";

        var (best, count) = counts.Where(c => c.Key is not (Script.Latin or Script.Other or Script.Kana or Script.Han or Script.Hangul))
            .OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).FirstOrDefault();
        if (count >= letters * Share)
            return best switch
            {
                Script.Cyrillic => text.IndexOfAny(['і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ']) >= 0 ? "uk"
                    : text.IndexOfAny(['ђ', 'љ', 'њ', 'ћ', 'џ', 'Ђ', 'Љ', 'Њ', 'Ћ', 'Џ']) >= 0 ? "sr"
                    : text.IndexOfAny(['ѓ', 'ќ', 'ѕ', 'Ѓ', 'Ќ', 'Ѕ']) >= 0 ? "mk"
                    : text.IndexOfAny(['ы', 'э', 'ё', 'Ы', 'Э', 'Ё']) < 0 && text.Contains('ъ') ? "bg"
                    : "ru",
                Script.Greek => "el",
                Script.Arabic => text.IndexOfAny(['ٹ', 'ڈ', 'ڑ', 'ں', 'ے']) >= 0 ? "ur" : text.IndexOfAny(['پ', 'چ', 'ژ', 'گ']) >= 0 ? "fa" : "ar",
                Script.Hebrew => "he",
                Script.Thai => "th",
                Script.Lao => "lo",
                Script.Khmer => "km",
                Script.Myanmar => "my",
                Script.Devanagari => "hi",
                Script.Bengali => "bn",
                Script.Gurmukhi => "pa",
                Script.Gujarati => "gu",
                Script.Tamil => "ta",
                Script.Telugu => "te",
                Script.Kannada => "kn",
                Script.Malayalam => "ml",
                Script.Sinhala => "si",
                Script.Georgian => "ka",
                Script.Armenian => "hy",
                _ => null,
            };

        return Count(Script.Latin) >= letters * 0.6 ? LatinLanguage(lines) : null;
    }

    /// <summary>The script a language is written in, for reading it (Latin for those that need no reading).</summary>
    public static Script ScriptOf(string language) => language switch
    {
        "ja" => Script.Kana,
        "zh" => Script.Han,
        "ko" => Script.Hangul,
        "ru" or "uk" or "sr" or "bg" or "mk" or "be" or "kk" => Script.Cyrillic,
        "el" => Script.Greek,
        "ar" or "fa" or "ur" => Script.Arabic,
        "he" or "yi" => Script.Hebrew,
        "th" => Script.Thai,
        "lo" => Script.Lao,
        "km" => Script.Khmer,
        "my" => Script.Myanmar,
        "hi" or "mr" or "ne" => Script.Devanagari,
        "bn" => Script.Bengali,
        "pa" => Script.Gurmukhi,
        "gu" => Script.Gujarati,
        "ta" => Script.Tamil,
        "te" => Script.Telugu,
        "kn" => Script.Kannada,
        "ml" => Script.Malayalam,
        "si" => Script.Sinhala,
        "ka" => Script.Georgian,
        "hy" => Script.Armenian,
        _ => Script.Latin,
    };

    // —— Lyrics in the Latin alphabet: their language from their commonest words ——

    private static readonly Dictionary<string, string[]> CommonWords = new()
    {
        ["en"] = ["the", "and", "you", "i", "to", "me", "my", "it", "is", "in", "your", "of", "that", "love", "don't", "be", "we", "oh"],
        ["es"] = ["que", "el", "la", "de", "y", "te", "mi", "no", "en", "me", "tu", "lo", "por", "un", "una", "amor", "yo", "como", "es", "se"],
        ["pt"] = ["que", "o", "a", "de", "e", "eu", "você", "não", "meu", "minha", "do", "da", "em", "um", "uma", "com", "é", "te", "amor", "pra"],
        ["fr"] = ["je", "tu", "le", "la", "les", "et", "de", "que", "est", "pas", "mon", "ma", "moi", "toi", "un", "une", "dans", "c'est", "amour"],
        ["de"] = ["ich", "du", "und", "die", "der", "das", "nicht", "ist", "mich", "dich", "mein", "ein", "es", "wir", "zu", "in", "mit", "liebe"],
        ["it"] = ["che", "il", "la", "di", "e", "non", "mi", "ti", "io", "tu", "un", "una", "per", "sono", "amore", "con", "del", "è", "ma"],
    };

    /// <summary>Spanish, English, Portuguese, French, German or Italian, when its commonest words clearly say so; null otherwise.</summary>
    public static string? LatinLanguage(IReadOnlyList<string> lines)
    {
        var words = string.Join(' ', lines).ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ';', ':', '"', '(', ')', '\n', '¿', '¡', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Normalize(NormalizationForm.FormC)).ToList();
        if (words.Count < 8)
            return null;
        var scores = CommonWords.ToDictionary(p => p.Key, p => words.Count(w => p.Value.Contains(w)));
        var ranked = scores.OrderByDescending(s => s.Value).ToList();
        // Clearly ahead, and with enough of its words.
        return ranked[0].Value >= Math.Max(3, words.Count * 0.08) && ranked[0].Value >= ranked[1].Value * 1.3 ? ranked[0].Key : null;
    }
}
