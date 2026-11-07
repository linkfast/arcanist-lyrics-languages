using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcanist.Extensibility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Translation of lyrics for Arcanist: with the administrator's LLM when there is one (much better with songs), else
/// with the local model when it is chosen and downloaded; nothing otherwise (players then offer no translation).
/// </summary>
public sealed class LyricsTranslator(SettingsStore settings, Components components, LlmTranslator llm, LocalTranslator local) : ILyricsTranslator
{
    public IReadOnlyList<string> Languages
    {
        get
        {
            var current = settings.Current;
            return current.Translation switch
            {
                TranslationEngine.Llm when current.LlmConfigured => current.Languages,
                TranslationEngine.Local when components.LocalModelReady => current.Languages.Where(LocalTranslator.Supports).ToList(),
                _ => [],
            };
        }
    }

    public string Engine
    {
        get
        {
            var current = settings.Current;
            return current.Translation == TranslationEngine.Llm ? $"LLM · {current.LlmModel}" : "M2M100";
        }
    }

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, string? from, string to, CancellationToken cancellationToken) =>
        settings.Current.Translation == TranslationEngine.Llm
            ? llm.TranslateAsync(settings.Current, lines, from, to, cancellationToken)
            : local.TranslateAsync(lines, from ?? Scripts.LanguageOf(lines), to, settings.Current.Threads, cancellationToken);

    /// <summary>A language's name in English ("Spanish"), for the LLM.</summary>
    public static string NameOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }
}

/// <summary>
/// Translates with an LLM through an API compatible with OpenAI's chat completions (Ollama, LM Studio, OpenAI, and
/// most providers): the lines go numbered as JSON and must come back as many, in order.
/// </summary>
public sealed class LlmTranslator(IHttpClientFactory http, IDataProtectionProvider protection, ILogger<LlmTranslator> logger)
{
    public const string HttpClientName = "lyrics-languages-llm";

    private IDataProtector Protector => protection.CreateProtector("Arcanist.LyricsLanguages.LlmKey");

    public string Protect(string key) => Protector.Protect(key);

    private string? Key(LyricsLanguagesSettings settings)
    {
        if (settings.ProtectedLlmKey is not { Length: > 0 } stored)
            return null;
        try
        {
            return Protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(LyricsLanguagesSettings settings, IReadOnlyList<string> lines, string? from, string to,
        CancellationToken cancellationToken)
    {
        if (!settings.LlmConfigured)
            throw new InvalidOperationException("No LLM is set up.");
        var numbered = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        var source = from is null ? "" : $" from {LyricsTranslator.NameOf(from)}";
        var system =
            $"You translate song lyrics{source} into {LyricsTranslator.NameOf(to)}. Translate each line on its own, keeping its meaning, tone and imagery, " +
            "as a singer would understand it, not word for word. Keep empty lines empty, and names, brand names and interjections (oh, la la) as they are. " +
            $"Answer only with JSON: {{\"lines\": [...]}} with exactly {lines.Count} strings, the translation of each line in the same order.";
        var body = new JsonObject
        {
            ["model"] = settings.LlmModel,
            ["temperature"] = 0.3,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = numbered.ToJsonString() }),
        };

        for (var attempt = 1; ; attempt++)
        {
            var translated = await AskAsync(settings, body, cancellationToken);
            if (translated is not null && translated.Count == lines.Count)
                return translated;
            logger.LogInformation("The LLM gave {Count} lines of {Expected} (attempt {Attempt})", translated?.Count, lines.Count, attempt);
            if (attempt == 2)
                return translated ?? [];
        }
    }

    /// <summary>A short request to check the address, model and key work: the translation of one line.</summary>
    public async Task<string> TestAsync(LyricsLanguagesSettings settings, CancellationToken cancellationToken)
    {
        var lines = await TranslateAsync(settings, ["Hello, my friend"], "en", "es", cancellationToken);
        return lines.FirstOrDefault() ?? "";
    }

    private async Task<List<string>?> AskAsync(LyricsLanguagesSettings settings, JsonObject body, CancellationToken cancellationToken)
    {
        var url = settings.LlmUrl!.EndsWith("/chat/completions", StringComparison.Ordinal) ? settings.LlmUrl : settings.LlmUrl + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (Key(settings) is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The LLM answered {(int)response.StatusCode}: {Trim(await response.Content.ReadAsStringAsync(cancellationToken))}");

        var answer = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        var content = (string?)answer?["choices"]?[0]?["message"]?["content"];
        return Lines(content);
    }

    /// <summary>The lines in an answer: <c>{"lines": [...]}</c>, a bare array, or JSON inside a code block.</summary>
    public static List<string>? Lines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        var text = content.Trim();
        var start = text.IndexOfAny(['{', '[']);
        var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
        if (start < 0 || end <= start)
            return null;
        try
        {
            var node = JsonNode.Parse(text[start..(end + 1)]);
            var array = node as JsonArray ?? node?["lines"] as JsonArray ?? (node as JsonObject)?.Select(p => p.Value).OfType<JsonArray>().FirstOrDefault();
            return array?.Select(item => item is JsonValue value && value.TryGetValue<string>(out var s) ? s : item?.ToString() ?? "").ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
