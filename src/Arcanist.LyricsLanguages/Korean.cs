using System.Text;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Korean in the Revised Romanization, as it sounds within each word: a final consonant moves to the next syllable
/// when that one starts with a vowel (한국어: hangugeo), finals before ㄴ, ㅁ and ㄹ become nasal (감사합니다:
/// gamsahamnida), ㄹ is r before a vowel and l elsewhere (사랑: sarang; 할래: hallae), and ㅎ makes the consonant next to
/// it aspirated (좋다: jota).
/// </summary>
public static class Korean
{
    private const int First = 0xAC00;
    private const int Last = 0xD7A3;

    // Initials: ㄱ ㄲ ㄴ ㄷ ㄸ ㄹ ㅁ ㅂ ㅃ ㅅ ㅆ ㅇ ㅈ ㅉ ㅊ ㅋ ㅌ ㅍ ㅎ
    private static readonly string[] Initials = ["g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"];

    private static readonly string[] Vowels =
        ["a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i"];

    // Finals (0: none): ㄱ ㄲ ㄳ ㄴ ㄵ ㄶ ㄷ ㄹ ㄺ ㄻ ㄼ ㄽ ㄾ ㄿ ㅀ ㅁ ㅂ ㅄ ㅅ ㅆ ㅇ ㅈ ㅊ ㅋ ㅌ ㅍ ㅎ, as heard at the end of a word.
    private static readonly string[] FinalSounds =
        ["", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", ""];

    /// <summary>
    /// A final before a vowel: what is heard where it is (as a final, by index into <see cref="FinalSounds"/>; -2 is ng),
    /// and the initial the next syllable takes (by index into <see cref="Initials"/>; -1 none).
    /// </summary>
    private static readonly (int Stays, int Moves)[] Liaison =
    [
        (0, -1), (0, 0), (0, 1), (1, 9), (0, 2), (4, 12), (0, 2), (0, 3), (0, 5), (8, 0), (8, 6), (8, 7), (8, 9), (8, 16), (8, 17), (0, 5),
        (0, 6), (0, 7), (17, 9), (0, 9), (0, 10), (-2, -1), (0, 12), (0, 14), (0, 15), (0, 16), (0, 17), (0, -1),
    ];

    private const int SilentInitial = 11;
    private const int Rieul = 5;
    private const int Nieun = 2;
    private const int Mieum = 6;
    private const int Hieut = 18;
    private const int FinalHieut = 27;

    public static bool IsHangul(char c) => c is >= (char)First and <= (char)Last;

    public static string Romanize(string text)
    {
        var output = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (!IsHangul(text[i]))
            {
                output.Append(text[i]);
                i++;
                continue;
            }

            // A word: the syllables until something else (the rules only apply within one).
            var end = i;
            while (end < text.Length && IsHangul(text[end]))
                end++;
            output.Append(Word(text[i..end]));
            i = end;
        }

        return output.ToString();
    }

    private static string Word(string word)
    {
        var syllables = word.Select(c =>
        {
            var index = c - First;
            return (Initial: index / (21 * 28), Vowel: index % (21 * 28) / 28, Final: index % 28);
        }).ToArray();
        // How each syllable's initial is heard, once the final before it changed it.
        var heardInitial = new string?[syllables.Length];

        var output = new StringBuilder();
        for (var s = 0; s < syllables.Length; s++)
        {
            var (initial, vowel, final) = syllables[s];
            output.Append(heardInitial[s] ?? Initials[initial]).Append(Vowels[vowel]);
            if (final == 0)
                continue;
            if (s + 1 == syllables.Length)
            {
                output.Append(FinalSounds[final]);
                continue;
            }

            var next = syllables[s + 1].Initial;
            if (next == SilentInitial)
            {
                var (stays, moves) = Liaison[final];
                output.Append(stays == -2 ? "ng" : FinalSounds[stays]);
                if (moves >= 0)
                    heardInitial[s + 1] = Initials[moves];
                continue;
            }

            if (final == FinalHieut && next is 0 or 3 or 12)
            {
                // ㅎ + ㄱ/ㄷ/ㅈ: ㅋ/ㅌ/ㅊ.
                heardInitial[s + 1] = next switch { 0 => "k", 3 => "t", _ => "ch" };
                continue;
            }

            var sound = FinalSounds[final];
            if (next == Hieut && sound is "k" or "t" or "p")
            {
                // ㄱ/ㄷ/ㅂ + ㅎ: aspirated.
                heardInitial[s + 1] = sound;
                continue;
            }

            if (next is Nieun or Mieum or Rieul)
                sound = sound switch { "k" => "ng", "t" => "n", "p" => "m", _ => sound };
            if (next == Rieul)
            {
                // ㄹ + ㄹ and ㄴ + ㄹ: ll; after other finals ㄹ is heard n.
                if (sound is "l" or "n")
                    (sound, heardInitial[s + 1]) = ("l", "l");
                else
                    heardInitial[s + 1] = "n";
            }
            else if (next == Nieun && sound == "l")
                heardInitial[s + 1] = "l";

            output.Append(sound);
        }

        return output.ToString();
    }
}
