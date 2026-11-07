using System.Globalization;
using System.Text.Json;
using Arcanist.Extensibility;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// The extension's page in Arcanist's admin panel (Extensions › Lyrics Languages): what it needs, how lyrics are read,
/// and what translates them.
/// </summary>
public sealed class LyricsLanguagesAdminPage(
    Components components,
    SettingsStore settings,
    LlmTranslator llm,
    LyricsTranslator translator) : IExtensionAdminPage
{
    /// <summary>The languages offered in the list (the ones M2M100 knows; an LLM knows them all).</summary>
    private static readonly string[] Offered =
    [
        "en", "es", "pt", "fr", "de", "it", "nl", "sv", "no", "da", "fi", "pl", "cs", "sk", "hu", "ro", "bg", "el", "ru", "uk", "sr", "hr", "tr", "ar",
        "he", "fa", "hi", "bn", "ta", "th", "vi", "id", "ms", "tl", "ja", "ko", "zh", "ca", "gl", "eu", "ga", "cy", "is", "lt", "lv", "et", "sl", "ka",
        "hy", "sw",
    ];

    public Task<AdminPage> GetAsync(AdminPageContext context, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        var missing = components.Missing(current);

        var status = new List<AdminBlock>();
        if (components.Progress is { } progress)
            status.Add(AdminBlock.Progress(t.Downloading(progress.Name), progress.Total > 0 ? 100.0 * progress.Done / progress.Total : 0,
                $"{progress.Done / 1048576} / {progress.Total / 1048576} MB"));
        else if (missing.Count > 0)
            status.Add(AdminBlock.Status(t.Components, t.ToDownload(missing.Sum(c => c.Size)), AdminTone.Warning));
        if (components.LastError is { } error)
            status.Add(AdminBlock.Notice(t.DownloadFailed(error), AdminTone.Bad));

        status.Add(AdminBlock.Status(t.ReadingsLabel,
            !current.Readings ? t.Off
            : components.IsInstalled(Components.Dictionary) ? t.ReadingsReady(current.Furigana)
            : t.ReadingsWithoutJapanese,
            !current.Readings ? AdminTone.Neutral : components.IsInstalled(Components.Dictionary) ? AdminTone.Good : AdminTone.Warning));

        var languages = translator.Languages;
        status.Add(AdminBlock.Status(t.TranslationLabel,
            current.Translation switch
            {
                TranslationEngine.Off => t.Off,
                TranslationEngine.Llm when !current.LlmConfigured => t.LlmMissing,
                TranslationEngine.Local when Components.Rid is null => t.Unsupported,
                TranslationEngine.Local when !components.LocalModelReady => t.ModelMissing,
                _ => t.Translating(translator.Engine, languages.Count),
            },
            current.Translation == TranslationEngine.Off ? AdminTone.Neutral : languages.Count > 0 ? AdminTone.Good : AdminTone.Warning));
        if (current.Translation == TranslationEngine.Local)
            status.Add(AdminBlock.Notice(t.LocalQuality, AdminTone.Neutral));

        var actions = new List<AdminAction>();
        if (missing.Count > 0 && !components.Installing)
            actions.Add(new AdminAction("install", t.Install, AdminActionStyle.Primary));
        if (current.Translation == TranslationEngine.Llm && current.LlmConfigured)
            actions.Add(new AdminAction("testLlm", t.TestLlm));

        var engines = Enum.GetValues<TranslationEngine>().Select(e => new AdminOption(e.ToString(), t.Engine(e))).ToList();
        var offered = Offered.Union(current.Languages).Select(code => new AdminOption(code, Name(code))).ToList();
        return Task.FromResult(new AdminPage(
        [
            new AdminSection("status", t.StatusTitle, t.StatusHelp, status, actions),
            new AdminSection("readings", t.ReadingsTitle, t.ReadingsHelp,
            [
                AdminBlock.Field("readings", t.Readings, AdminFieldType.Switch, current.Readings, t.ReadingsSwitchHelp),
                AdminBlock.Field("furigana", t.Furigana, AdminFieldType.Switch, current.Furigana, t.FuriganaHelp),
            ]),
            new AdminSection("translation", t.TranslationTitle, t.TranslationHelp,
            [
                AdminBlock.Field("translation", t.EngineLabel, AdminFieldType.Select, current.Translation.ToString(), t.EngineHelp, options: engines),
                AdminBlock.Field("llmUrl", t.LlmUrl, AdminFieldType.Url, current.LlmUrl, t.LlmUrlHelp, placeholder: "http://192.168.1.10:11434/v1"),
                AdminBlock.Field("llmModel", t.LlmModel, AdminFieldType.Text, current.LlmModel, t.LlmModelHelp, placeholder: "llama3.1:8b"),
                AdminBlock.Field("llmKey", t.LlmKey, AdminFieldType.Secret, "", current.ProtectedLlmKey is null ? t.LlmKeyHelp : t.LlmKeySaved),
                AdminBlock.Field("languages", t.Languages, AdminFieldType.MultiSelect, current.Languages, t.LanguagesHelp, options: offered),
                AdminBlock.Field("threads", t.Threads, AdminFieldType.Number, current.Threads, t.ThreadsHelp, min: 1, max: LyricsLanguagesSettings.MaxThreads),
            ]),
            new AdminSection("about", t.AboutTitle, null, [AdminBlock.Notice(t.About)]),
        ], RefreshSeconds: components.Installing ? 3 : null)
        {
            // Offered by the panel right after installing the extension, with the administrator's consent.
            Setup = missing.Count == 0 || components.Installing
                ? null
                : new AdminSetup(t.SetupTitle, t.SetupDescription(missing.Sum(c => c.Size)), missing.Select(c => $"{c.Name} ({c.Size / 1048576} MB)").ToList(),
                    "install", t.InstallNow),
        });
    }

    public Task<AdminPageResult> SaveAsync(AdminPageContext context, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        bool? Switch(string key) => values.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        string? Text(string key) => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var engine = current.Translation;
        if (Text("translation") is { } chosen && !Enum.TryParse(chosen, out engine))
            return Task.FromResult(AdminPageResult.Failed(t.Invalid, new Dictionary<string, string> { ["translation"] = t.Invalid }));
        var url = values.ContainsKey("llmUrl") ? Text("llmUrl") : current.LlmUrl;
        if (!string.IsNullOrWhiteSpace(url) && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
            return Task.FromResult(AdminPageResult.Failed(t.Invalid, new Dictionary<string, string> { ["llmUrl"] = t.InvalidUrl }));
        var languages = values.TryGetValue("languages", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : current.Languages;

        var saved = (current with
        {
            Readings = Switch("readings") ?? current.Readings,
            Furigana = Switch("furigana") ?? current.Furigana,
            Translation = engine,
            LlmUrl = url,
            LlmModel = values.ContainsKey("llmModel") ? Text("llmModel") : current.LlmModel,
            // An empty key keeps the one there is.
            ProtectedLlmKey = Text("llmKey") is { Length: > 0 } key ? llm.Protect(key.Trim()) : current.ProtectedLlmKey,
            Languages = languages,
            Threads = values.TryGetValue("threads", out var threads) && threads.ValueKind == JsonValueKind.Number ? threads.GetInt32() : current.Threads,
        }).Normalized();
        settings.Save(saved);
        components.RemoveUnused(saved);
        // What the new settings need comes down now (the administrator already agreed to the extension's downloads).
        if (components.Missing(saved).Count > 0)
            components.StartInstall(saved);
        return Task.FromResult(AdminPageResult.Done(t.Saved));
    }

    public async Task<AdminPageResult> RunAsync(AdminPageContext context, string action, IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        switch (action)
        {
            case "install":
                components.StartInstall(settings.Current);
                return AdminPageResult.Done(t.InstallStarted);
            case "testLlm":
                try
                {
                    var answer = await llm.TestAsync(settings.Current, cancellationToken);
                    return string.IsNullOrWhiteSpace(answer) ? AdminPageResult.Failed(t.LlmEmpty) : AdminPageResult.Done(t.LlmWorks(answer));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
                {
                    return AdminPageResult.Failed(t.LlmFailed(e.Message));
                }
            default:
                return AdminPageResult.Failed(t.Invalid);
        }
    }

    private static string Name(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            var native = culture.NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], culture) + native[1..] + $" ({code})" : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>The page's words, in Spanish or English.</summary>
    private sealed record Texts(bool Es)
    {
        public static Texts For(string language) => new(language == "es");

        private string S(string es, string en) => Es ? es : en;

        public string StatusTitle => S("Estado", "Status");
        public string StatusHelp => S("Los reproductores muestran la pronunciación junto a las letras y piden su traducción; aquí se ve qué está listo.",
            "Players show the pronunciation next to the lyrics and ask for their translation; this is what is ready.");
        public string Components => S("Componentes", "Components");
        public string ToDownload(long bytes) => S($"Faltan por descargar ({bytes / 1048576} MB)", $"To download ({bytes / 1048576} MB)");
        public string Downloading(string name) => S($"Descargando {name}", $"Downloading {name}");
        public string DownloadFailed(string error) => S($"La descarga falló: {error}", $"The download failed: {error}");
        public string Off => S("Apagado", "Off");
        public string ReadingsLabel => S("Pronunciación", "Pronunciation");
        public string ReadingsReady(bool furigana) => furigana
            ? S("Japonés (romaji y furigana), chino, coreano y otras escrituras", "Japanese (romaji and furigana), Chinese, Korean and other scripts")
            : S("Japonés (romaji), chino, coreano y otras escrituras", "Japanese (romaji), Chinese, Korean and other scripts");
        public string ReadingsWithoutJapanese => S("Chino, coreano y otras escrituras; el japonés espera su diccionario",
            "Chinese, Korean and other scripts; Japanese waits for its dictionary");
        public string TranslationLabel => S("Traducción", "Translation");
        public string LlmMissing => S("Falta la dirección y el modelo del LLM", "The LLM's address and model are missing");
        public string ModelMissing => S("El modelo local aún no está descargado", "The local model is not downloaded yet");
        public string Unsupported => S("No hay modelo local para este sistema", "There is no local model for this system");
        public string Translating(string engine, int count) => S($"{engine} · {count} idiomas", $"{engine} · {count} languages");
        public string LocalQuality => S(
            "El modelo local traduce de forma literal: sirve para entender una letra, no para cantarla. Un LLM (por ejemplo, Ollama en tu red) traduce las canciones mucho mejor.",
            "The local model translates literally: enough to understand lyrics, not to sing them. An LLM (Ollama on your network, for instance) translates songs much better.");
        public string Install => S("Descargar lo que falta", "Download what is missing");
        public string InstallNow => S("Descargar ahora", "Download now");
        public string InstallStarted => S("Descargando en segundo plano.", "Downloading in the background.");
        public string TestLlm => S("Probar el LLM", "Test the LLM");
        public string LlmWorks(string answer) => S($"El LLM respondió: «{answer}»", $"The LLM answered: “{answer}”");
        public string LlmEmpty => S("El LLM no devolvió una traducción.", "The LLM gave no translation.");
        public string LlmFailed(string error) => S($"El LLM no respondió bien: {error}", $"The LLM did not answer well: {error}");

        public string ReadingsTitle => S("Pronunciación", "Pronunciation");
        public string ReadingsHelp => S(
            "Para las letras en escrituras que muchos no leen: japonés en romaji (y furigana sobre los kanji), chino en pinyin, coreano romanizado, y cirílico, griego, árabe, hebreo, tailandés o hindi en alfabeto latino.",
            "For lyrics in scripts many cannot read: Japanese in romaji (and furigana over the kanji), Chinese in pinyin, Korean romanized, and Cyrillic, Greek, Arabic, Hebrew, Thai or Hindi in the Latin alphabet.");
        public string Readings => S("Mostrar la pronunciación", "Show the pronunciation");
        public string ReadingsSwitchHelp => S("El japonés necesita un diccionario de 14 MB (se descarga con tu permiso).",
            "Japanese needs a 14 MB dictionary (downloaded with your consent).");
        public string Furigana => S("Furigana en el japonés", "Furigana in Japanese");
        public string FuriganaHelp => S("La lectura en hiragana encima de cada palabra con kanji.", "The reading in hiragana above each word with kanji.");

        public string TranslationTitle => S("Traducción", "Translation");
        public string TranslationHelp => S(
            "Un usuario pide la letra en otro idioma desde su reproductor; se traduce una vez y queda guardada para todos.",
            "A user asks for the lyrics in another language from their player; they are translated once and kept for everyone.");
        public string EngineLabel => S("Traducir con", "Translate with");
        public string EngineHelp => S(
            "El modelo local (M2M100, unos 630 MB) funciona sin internet pero traduce de forma literal. Un LLM compatible con la API de OpenAI (Ollama, LM Studio o un proveedor con clave) traduce mucho mejor; con uno externo, las letras salen de tu servidor.",
            "The local model (M2M100, about 630 MB) works offline but translates literally. An LLM with an API compatible with OpenAI's (Ollama, LM Studio or a provider with a key) translates much better; with an external one, the lyrics leave your server.");
        public string Engine(TranslationEngine engine) => engine switch
        {
            TranslationEngine.Off => S("Nada (sin traducción)", "Nothing (no translation)"),
            TranslationEngine.Local => S("Modelo local (M2M100)", "Local model (M2M100)"),
            _ => S("Un LLM", "An LLM"),
        };
        public string LlmUrl => S("Dirección del LLM", "LLM address");
        public string LlmUrlHelp => S("La de su API compatible con OpenAI, con /v1 (Ollama: http://<equipo>:11434/v1).",
            "Its OpenAI-compatible API, with /v1 (Ollama: http://<host>:11434/v1).");
        public string LlmModel => S("Modelo", "Model");
        public string LlmModelHelp => S("Como lo nombra ese servicio (llama3.1:8b, qwen2.5:7b, gpt-4o-mini…).",
            "As that service names it (llama3.1:8b, qwen2.5:7b, gpt-4o-mini…).");
        public string LlmKey => S("Clave de la API", "API key");
        public string LlmKeyHelp => S("Solo si el servicio la pide. Se guarda cifrada y nunca se muestra.", "Only if the service asks for one. Kept encrypted, never shown.");
        public string LlmKeySaved => S("Hay una guardada; déjalo vacío para conservarla.", "One is saved; leave it empty to keep it.");
        public string Languages => S("Idiomas que se ofrecen", "Languages offered");
        public string LanguagesHelp => S("Los que los reproductores ofrecen para traducir.", "Those players offer to translate into.");
        public string Threads => S("Hilos del modelo local", "Local model threads");
        public string ThreadsHelp => S("Más hilos traducen antes, pero dejan menos CPU al resto del servidor.", "More threads translate sooner but leave less CPU to the rest of the server.");

        public string AboutTitle => S("Acerca de", "About");
        public string About => S(
            "Lyrics Languages usa MeCab con el diccionario IPADIC para el japonés, las transliteraciones de ICU para las demás escrituras y M2M100 (Meta, licencia MIT) como modelo local. Las traducciones automáticas pueden equivocarse.",
            "Lyrics Languages uses MeCab with the IPADIC dictionary for Japanese, ICU's transliterations for other scripts and M2M100 (Meta, MIT license) as its local model. Machine translations can be wrong.");

        public string SetupTitle => S("Preparar Lyrics Languages", "Set up Lyrics Languages");
        public string SetupDescription(long bytes) => S(
            $"Para mostrar la pronunciación y traducir, la extensión descarga lo siguiente ({bytes / 1048576} MB), comprobado con su SHA-256:",
            $"To show pronunciation and translate, the extension downloads the following ({bytes / 1048576} MB), checked against its SHA-256:");
        public string Saved => S("Ajustes guardados", "Settings saved");
        public string Invalid => S("Valor no válido", "Invalid value");
        public string InvalidUrl => S("Debe ser una dirección http o https.", "It must be an http or https address.");
    }
}
