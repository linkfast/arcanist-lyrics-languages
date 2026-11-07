using Microsoft.Extensions.Logging.Abstractions;

namespace Arcanist.LyricsLanguages.Tests;

public sealed class ReadingTests : IDisposable
{
    /// <summary>The IPADIC dictionary MeCab.DotNet's package puts beside the tests.</summary>
    private static readonly string Dictionary = Path.Combine(AppContext.BaseDirectory, "dic");

    private readonly Japanese _japanese = new(Dictionary);

    public void Dispose() => _japanese.Dispose();

    [Theory]
    [InlineData("君の名前を呼んだ", "kimi no namae o yonda")]
    [InlineData("夜空に光る星を見ていた", "yozora ni hikaru hoshi o mite ita")]
    [InlineData("私は東京へ行きたい", "watashi wa toukyou e ikitai")]
    [InlineData("ちょっと待って", "chotto matte")]
    [InlineData("ラーメンが好き！", "raamen ga suki!")]
    public void Japanese_comes_in_romaji_with_particles_read_as_they_sound(string line, string romaji)
    {
        Assert.Equal(romaji, Japanese.Romaji(_japanese.Words(line)));
    }

    [Fact]
    public void Kanji_get_their_reading_above_them_without_repeating_their_kana()
    {
        var ruby = Japanese.Ruby(_japanese.Words("夜空に光る星"))!;

        Assert.Equal([("夜空", "よぞら"), ("に", null), ("光", "ひか"), ("る", null), ("星", "ほし")], ruby.Select(s => (s.Text, s.Reading)));
        // A line without kanji needs none.
        Assert.Null(Japanese.Ruby(_japanese.Words("きみのなまえ")));
    }

    [Theory]
    [InlineData("きって", "kitte")]
    [InlineData("まっちゃ", "matcha")]
    [InlineData("きんえん", "kin'en")]
    [InlineData("しゃしん", "shashin")]
    [InlineData("ファン", "fan")]
    public void Kana_follow_Hepburn(string kana, string romaji) => Assert.Equal(romaji, Japanese.ToRomaji(kana));

    [Theory]
    [InlineData("사랑해요 그대를", "saranghaeyo geudaereul")]
    [InlineData("감사합니다", "gamsahamnida")]
    [InlineData("한국어", "hangugeo")]
    [InlineData("좋다", "jota")]
    [InlineData("신라", "silla")]
    [InlineData("종로", "jongno")]
    [InlineData("많아", "mana")]
    public void Korean_is_romanized_as_it_sounds(string text, string romanized) => Assert.Equal(romanized, Korean.Romanize(text));

    [Theory]
    [InlineData("ja", "君の名前を呼んだ", "夜空に光る星")]
    [InlineData("zh", "月亮代表我的心", "你问我爱你有多深")]
    [InlineData("ko", "사랑해요 그대를", "보고 싶어요")]
    [InlineData("ru", "Я тебя люблю", "Ты моя звезда")]
    [InlineData("uk", "Я тебе кохаю", "Ти моя зірка і їй")]
    [InlineData("el", "Σ' αγαπώ", "είσαι το αστέρι μου")]
    [InlineData("ar", "أحبك يا قمري", "أنت نجمتي")]
    [InlineData("he", "אני אוהב אותך", "את הכוכב שלי")]
    [InlineData("hi", "मैं तुमसे प्यार करता हूँ", "तुम मेरा सितारा हो")]
    [InlineData("es", "Te quiero, mi amor, y no me voy", "que la noche es larga y el amor es mío")]
    [InlineData("en", "I love you and you love me", "the night is long and the stars are mine")]
    public void The_language_comes_from_the_script_and_in_the_Latin_alphabet_from_common_words(string language, params string[] lines) =>
        Assert.Equal(language, Scripts.LanguageOf(lines));

    [Fact]
    public void A_few_words_of_another_script_do_not_make_lyrics_need_reading()
    {
        Assert.Null(Scripts.LanguageOf(["Hello hello", "sayonara"]));
        Assert.NotEqual("ja", Scripts.LanguageOf(["I keep walking down this lonely road tonight", "and I say 愛してる to you again and again my friend"]));
    }

    [Fact]
    public void Other_scripts_go_into_the_Latin_alphabet_with_ICU()
    {
        var paths = new ExtensionPaths(Path.Combine(Path.GetTempPath(), "arcanist-lyrics-languages-" + Guid.NewGuid().ToString("N")));
        using var readings = new LyricsReadings(new SettingsStore(paths), new Components(paths, null!, NullLogger<Components>.Instance));

        Assert.Equal(["yuè liàng dài biǎo wǒ de xīn"], Romanized(readings, "zh", "月亮代表我的心"));
        Assert.Equal(["Ya tebya lyublyu"], Romanized(readings, "ru", "Я тебя люблю"));
        Assert.Equal(["saranghaeyo geudaereul"], Romanized(readings, "ko", "사랑해요 그대를"));
        Assert.NotEmpty(Romanized(readings, "el", "Σ' αγαπώ")[0]!);
        // Japanese without its dictionary: nothing, rather than wrong readings.
        Assert.Equal([null], readings.Read(["君の名前"], "ja"));
        // An English line in Chinese lyrics needs none.
        Assert.Equal([null], Romanized(readings, "zh", "Baby I love you"));
    }

    private static List<string?> Romanized(LyricsReadings readings, string language, params string[] lines) =>
        readings.Read(lines, language).Select(r => r?.Romanized).ToList();
}
