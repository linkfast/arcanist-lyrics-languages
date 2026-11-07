using System.Text;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Cyrillic in the Latin alphabet as English speakers read it (BGN/PCGN, without diacritics): Я тебя люблю is
/// "Ya tebya lyublyu". Each language's own letters and sounds: Ukrainian и is y and г is h, Bulgarian щ is sht and ъ is a,
/// Serbian and Macedonian follow their own Latin alphabets.
/// </summary>
public static class Cyrillic
{
    private static readonly Dictionary<char, string> Russian = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        // Letters of other languages written in Cyrillic, when they turn up.
        ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g", ['ў'] = "w", ['ђ'] = "đ", ['ј'] = "j", ['љ'] = "lj", ['њ'] = "nj", ['ћ'] = "ć",
        ['џ'] = "dž", ['ѓ'] = "gj", ['ќ'] = "kj", ['ѕ'] = "dz", ['қ'] = "q", ['ғ'] = "gh", ['ң'] = "ng", ['ө'] = "ö", ['ү'] = "ü", ['ұ'] = "u",
        ['һ'] = "h", ['ә'] = "ä",
    };

    private static readonly Dictionary<string, Dictionary<char, string>> Languages = new()
    {
        ["uk"] = new() { ['г'] = "h", ['и'] = "y", ['й'] = "y", ['щ'] = "shch", ['є'] = "ye", ['ї'] = "yi", ['ю'] = "yu", ['я'] = "ya", ['ь'] = "" },
        ["bg"] = new() { ['щ'] = "sht", ['ъ'] = "a", ['ю'] = "yu", ['я'] = "ya", ['ь'] = "y" },
        ["sr"] = new() { ['ж'] = "ž", ['ч'] = "č", ['ш'] = "š", ['ц'] = "c", ['х'] = "h", ['ј'] = "j" },
        ["mk"] = new() { ['ж'] = "ž", ['ч'] = "č", ['ш'] = "š", ['ц'] = "c", ['х'] = "h", ['ј'] = "j", ['ѓ'] = "gj", ['ќ'] = "kj" },
    };

    public static string Romanize(string text, string language)
    {
        var own = Languages.GetValueOrDefault(language);
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lower = char.ToLowerInvariant(c);
            var latin = own?.GetValueOrDefault(lower) ?? Russian.GetValueOrDefault(lower);
            if (latin is null)
            {
                output.Append(c);
                continue;
            }

            // Russian е is ye at the start of a word and after a vowel or a sign (ель: yel').
            if (language is "ru" or "be" && lower == 'е' && (i == 0 || !char.IsLetter(text[i - 1]) || "аеёиоуыэюяъь".Contains(char.ToLowerInvariant(text[i - 1]))))
                latin = "ye";
            if (char.IsUpper(c) && latin.Length > 0)
            {
                // Я alone or in a word that is all capitals: YA; at the start of a word: Ya.
                var allCapitals = i + 1 < text.Length && char.IsUpper(text[i + 1]);
                latin = allCapitals ? latin.ToUpperInvariant() : char.ToUpperInvariant(latin[0]) + latin[1..];
            }

            output.Append(latin);
        }

        return output.ToString();
    }
}
