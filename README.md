# Lyrics Languages for Arcanist

An official extension for [Arcanist](https://github.com/linkfast/arcanist), the self-hosted music server: players show
how lyrics in scripts many cannot read are pronounced, and can ask for the lyrics in another language.

- **Pronunciation next to each line.** Japanese in romaji (*kimi no namae o yonda*) and with furigana over the kanji,
  Chinese in pinyin with tones (*yuè liàng dài biǎo wǒ de xīn*), Korean in the Revised Romanization as it sounds
  (*saranghaeyo*), Cyrillic as English speakers read it (*Ya tebya lyublyu*), and Greek, Arabic, Hebrew, Thai, Hindi
  and other scripts in the Latin alphabet. The language comes from the script (Japanese has kana, Ukrainian has і and
  ї…); lyrics in the Latin alphabet need none, and their language (Spanish, English, Portuguese, French, German,
  Italian) is told from their commonest words.
- **Translation on request.** A listener asks for the lyrics in their language from the player; the server translates
  them once, in the background, and keeps the translation for everyone. Each translated line goes with its line, so
  synchronized lyrics stay synchronized.
  - **With an LLM** (recommended): any API compatible with OpenAI's chat completions, such as
    [Ollama](https://ollama.com) or LM Studio on your network, or a provider with a key. It translates songs well.
  - **With a local model**: [M2M100](https://huggingface.co/facebook/m2m100_418M) (418M, MIT) runs on the server,
    offline and private, about 630 MB. It translates literally: enough to understand, not to sing.
- **Nothing on disk changes.** Readings and translations live in Arcanist's database, by the text of the lyrics.

Arcanist 0.1.0-alpha.25 or newer (its lyrics API carries readings and translations). Players: Webamp for Arcanist
shows both; others read `TrackLyrics.readings`, `lyricsTranslation` and `requestLyricsTranslation` (see Arcanist's
developers' guide).

## Install

From Arcanist's panel: **Extensions › Marketplace › Lyrics Languages**. After the restart, the panel offers to
download the Japanese dictionary (14 MB, checked against its SHA-256). In **Extensions › Lyrics Languages** choose what
translates:

| | Size |
|---|---|
| IPADIC Japanese dictionary (pronunciation) | 14 MB |
| M2M100 local model and ONNX Runtime (only with the local model) | 600 + 10 MB |

The local model runs on 64-bit Windows, Linux (glibc) and macOS; a song takes from seconds (a modern CPU) to a few
minutes (a small NAS). The LLM takes what it takes that machine or provider.

## Settings

In **Extensions › Lyrics Languages**: pronunciation on or off, furigana for Japanese, what translates (nothing, the
local model or an LLM: its address, model and key, kept encrypted), the languages players offer, and the threads of
the local model. **Test the LLM** checks the address, model and key with one line.

## Develop

```bash
dotnet test --project tests/Arcanist.LyricsLanguages.Tests   # needs Arcanist checked out at ../../Arcanist (or ARCANIST_ROOT)
sh pack.sh                                                  # artifacts/arcanist-lyrics-languages-<version>.zip and its .sha256
```

The tests read Japanese with the dictionary MeCab.DotNet's package puts beside them, translate with a fake LLM, and run
the real local model only when `ARCANIST_M2M100` (the model's files: `encoder.onnx`, `decoder.onnx`,
`sentencepiece.bpe.model`, `vocab.json`) and `ARCANIST_ONNXRUNTIME` (the folder of ONNX Runtime's native library) are set.

## License

AGPL-3.0-or-later, like Arcanist. Copyright (C) 2026 LinkFast S.A. and Arcanist contributors. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
