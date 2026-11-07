using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcanist.LyricsLanguages;

/// <summary>What translates lyrics.</summary>
public enum TranslationEngine
{
    /// <summary>Nothing: players offer no translation.</summary>
    Off,

    /// <summary>M2M100, a model run on this server (downloaded when chosen, ~630 MB): private, works offline, literal.</summary>
    Local,

    /// <summary>An LLM with an API compatible with OpenAI's (Ollama on the network, or a provider with a key): much better.</summary>
    Llm,
}

/// <param name="Readings">Players get how lyrics in other scripts are pronounced.</param>
/// <param name="Furigana">Japanese lyrics also get the reading of their kanji above them.</param>
/// <param name="LlmUrl">The API's address, such as <c>http://192.168.1.10:11434/v1</c> (Ollama) or <c>https://api.openai.com/v1</c>.</param>
/// <param name="ProtectedLlmKey">The API key, protected with Data Protection; never shown back.</param>
/// <param name="Languages">The languages offered to translate into (ISO 639-1).</param>
/// <param name="Threads">CPU threads of the local model.</param>
public sealed record LyricsLanguagesSettings(
    bool Readings,
    bool Furigana,
    TranslationEngine Translation,
    string? LlmUrl,
    string? LlmModel,
    string? ProtectedLlmKey,
    IReadOnlyList<string> Languages,
    int Threads)
{
    public const int MaxThreads = 16;

    /// <summary>Offered by default: the languages most listeners read.</summary>
    public static readonly IReadOnlyList<string> DefaultLanguages = ["en", "es", "pt", "fr", "de", "it", "ja", "ko", "zh", "ru"];

    public static LyricsLanguagesSettings Default => new(true, true, TranslationEngine.Off, null, null, null, DefaultLanguages,
        Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public bool LlmConfigured => !string.IsNullOrWhiteSpace(LlmUrl) && !string.IsNullOrWhiteSpace(LlmModel);

    public LyricsLanguagesSettings Normalized() => this with
    {
        Threads = Math.Clamp(Threads, 1, MaxThreads),
        Languages = (Languages ?? DefaultLanguages).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length is >= 2 and <= 3).Distinct().ToList(),
        LlmUrl = string.IsNullOrWhiteSpace(LlmUrl) ? null : LlmUrl.Trim().TrimEnd('/'),
        LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? null : LlmModel.Trim(),
    };
}

/// <summary>The extension's settings, kept in its data folder.</summary>
public sealed class SettingsStore(ExtensionPaths paths)
{
    private readonly Lock _gate = new();
    private LyricsLanguagesSettings? _current;

    public LyricsLanguagesSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null)
                    return _current;
                try
                {
                    _current = File.Exists(paths.SettingsFile)
                        ? (JsonSerializer.Deserialize<LyricsLanguagesSettings>(File.ReadAllText(paths.SettingsFile), Json.Options) ?? LyricsLanguagesSettings.Default).Normalized()
                        : LyricsLanguagesSettings.Default;
                }
                catch (JsonException)
                {
                    _current = LyricsLanguagesSettings.Default;
                }

                return _current;
            }
        }
    }

    public void Save(LyricsLanguagesSettings settings)
    {
        lock (_gate)
        {
            Files.WriteAllTextAtomically(paths.SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
            _current = settings;
        }
    }
}

/// <summary>Where the extension keeps its things, inside its data folder.</summary>
public sealed class ExtensionPaths(string root)
{
    public string Root { get; } = root;

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The downloaded dictionary, model and native library.</summary>
    public string Components => Path.Combine(Root, "components");
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}

internal static class Files
{
    /// <summary>Writes beside the file and then replaces it, so a crash never leaves half a file.</summary>
    public static void WriteAllTextAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
