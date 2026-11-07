using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcanist.Core.Setup;
using Arcanist.Core.Tests;
using Arcanist.Core.Tests.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcanist.LyricsLanguages.Tests;

/// <summary>An LLM with an API like OpenAI's: it "translates" each line as <c>[es] line</c>, and says what it was asked.</summary>
public sealed class FakeLlm : IAsyncDisposable
{
    private readonly WebApplication _app;

    public List<(string? Authorization, JsonObject Body)> Calls { get; } = [];

    public string Url { get; }

    /// <summary>Answers with fewer lines than asked (a model that loses some).</summary>
    public bool Short { get; set; }

    private FakeLlm(WebApplication app, string url) => (_app, Url) = (app, url);

    public static async Task<FakeLlm> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeLlm? fake = null;
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            lock (fake!.Calls)
                fake.Calls.Add((context.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, body));
            var lines = JsonNode.Parse((string)body["messages"]![1]!["content"]!)!.AsArray().Select(l => (string)l!).ToList();
            var translated = lines.Select(l => l.Length == 0 ? "" : "[es] " + l).Take(fake.Short ? 1 : lines.Count);
            var content = new JsonObject { ["lines"] = new JsonArray(translated.Select(t => (JsonNode?)t).ToArray()) }.ToJsonString();
            return Results.Json(new { choices = new[] { new { message = new { role = "assistant", content = $"```json\n{content}\n```" } } } });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeLlm(app, address + "/v1");
        return fake;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// The extension installed in a server, as an administrator leaves it: lyrics come with how they are read, and a
/// translation asked for is made by the LLM set up in its page and kept for everyone.
/// </summary>
public sealed class ExtensionTests : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly string Extension = Path.Combine(AppContext.BaseDirectory, "extension");

    private readonly string _music = Directory.CreateTempSubdirectory("arcanist-lyrics-languages-music-").FullName;
    private ArcanistFactory _factory = null!;
    private HttpClient _admin = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        Directory.Delete(_music, recursive: true);
    }

    private string ExtensionData => Path.Combine(_factory.DataPath, "extension-data", "lyrics-languages");

    private async Task StartAsync(string? llm = null, bool dictionary = true)
    {
        AudioFixtures.WriteWav(Path.Combine(_music, "hoshi.wav"), 1, new FixtureTags("Hoshi", "Band"));
        File.WriteAllText(Path.Combine(_music, "hoshi.lrc"), "[00:01.00]君の名前を呼んだ\n[00:03.00]\n[00:05.00]夜空に光る星\n");
        AudioFixtures.WriteWav(Path.Combine(_music, "seoul.wav"), 2, new FixtureTags("Seoul", "Band", Lyrics: "사랑해요 그대를\n감사합니다"));
        AudioFixtures.WriteWav(Path.Combine(_music, "moskva.wav"), 3, new FixtureTags("Moskva", "Band", Lyrics: "Я тебя люблю"));
        AudioFixtures.WriteWav(Path.Combine(_music, "english.wav"), 4,
            new FixtureTags("English", "Band", Lyrics: "I love you and you love me\nthe night is long and the stars are mine"));

        _factory = new ArcanistFactory(new Dictionary<string, string?> { ["Arcanist:Extensions:lyrics-languages:Enabled"] = "true" });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-languages"));
        if (dictionary)
        {
            // The dictionary as set-up leaves it (the tests have it from MeCab.DotNet's package).
            CopyFolder(Path.Combine(AppContext.BaseDirectory, "dic"), Path.Combine(ExtensionData, "components", "ipadic"));
            File.WriteAllText(Path.Combine(ExtensionData, "components", ".ipadic.sha256"), Components.Dictionary.Sha256);
        }

        if (llm is not null)
            File.WriteAllText(Path.Combine(ExtensionData, "settings.json"), JsonSerializer.Serialize(new
            {
                readings = true, furigana = true, translation = "Llm", llmUrl = llm, llmModel = "test-model", languages = new[] { "es", "en" }, threads = 1,
            }));

        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<string> TrackAsync(string title) =>
        (await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks?search={title}", ArcanistFactory.Json, Cancel)).GetProperty("items")[0].GetProperty("id").GetString()!;

    private async Task<JsonElement> LyricsAsync(string title) =>
        await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks/{await TrackAsync(title)}/lyrics", ArcanistFactory.Json, Cancel);

    private static List<string?> Romanized(JsonElement lyrics) =>
        lyrics.GetProperty("readings").EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null : r.GetProperty("romanized").GetString()).ToList();

    [Fact]
    public async Task Lyrics_in_other_scripts_come_with_how_they_are_read()
    {
        await StartAsync();
        var features = (await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/server-info", ArcanistFactory.Json, Cancel)).GetProperty("features")
            .EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("LYRICS_READINGS", features);
        // Nothing translates until the administrator chooses an engine.
        Assert.DoesNotContain("LYRICS_TRANSLATION", features);

        var japanese = await LyricsAsync("Hoshi");
        Assert.Equal("ja", japanese.GetProperty("language").GetString());
        Assert.Equal(["kimi no namae o yonda", null, "yozora ni hikaru hoshi"], Romanized(japanese));
        var ruby = japanese.GetProperty("readings")[2].GetProperty("ruby");
        Assert.Equal(("夜空", "よぞら"), (ruby[0].GetProperty("text").GetString(), ruby[0].GetProperty("reading").GetString()));

        Assert.Equal(["saranghaeyo geudaereul", "gamsahamnida"], Romanized(await LyricsAsync("Seoul")));
        Assert.Equal(["Ya tebya lyublyu"], Romanized(await LyricsAsync("Moskva")));
        var english = await LyricsAsync("English");
        Assert.Equal(("en", JsonValueKind.Null), (english.GetProperty("language").GetString(), english.GetProperty("readings").ValueKind));
    }

    [Fact]
    public async Task A_translation_asked_for_is_made_by_the_LLM_and_kept_for_everyone()
    {
        await using var llm = await FakeLlm.StartAsync();
        await StartAsync(llm.Url);
        var hoshi = await TrackAsync("Hoshi");
        Assert.Equal(["es", "en"], (await _admin.GetFromJsonAsync<List<string>>("/rest/v1/lyrics/translation-languages", ArcanistFactory.Json, Cancel))!);

        var asked = await (await _admin.PostAsync($"/rest/v1/tracks/{hoshi}/lyrics/translations/es", null, Cancel)).Content
            .ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel);
        Assert.Contains(asked.GetProperty("status").GetString(), new[] { "QUEUED", "WORKING", "READY" });

        JsonElement ready = default;
        for (var tries = 0; tries < 200; tries++)
        {
            ready = await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks/{hoshi}/lyrics/translations/es", ArcanistFactory.Json, Cancel);
            if (ready.GetProperty("status").GetString() is "READY" or "FAILED")
                break;
            await Task.Delay(50, Cancel);
        }

        Assert.Equal(["[es] 君の名前を呼んだ", "", "[es] 夜空に光る星"], ready.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(("LLM · test-model", "ja"), (ready.GetProperty("engine").GetString(), ready.GetProperty("from").GetString()));
        // What the LLM was asked: Japanese into Spanish, the lines as JSON, with the model chosen.
        var (_, body) = Assert.Single(llm.Calls);
        Assert.Equal("test-model", (string?)body["model"]);
        Assert.Contains("from Japanese into Spanish", (string?)body["messages"]![0]!["content"]);
    }

    [Fact]
    public async Task The_admin_page_offers_to_download_what_is_missing_and_saves_the_LLM_key_without_showing_it()
    {
        await StartAsync(dictionary: false);
        var page = await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/extensions/lyrics-languages/admin?language=es", ArcanistFactory.Json, Cancel);
        Assert.Equal(["status", "readings", "translation", "about"], page.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("install", page.GetProperty("setup").GetProperty("action").GetString());

        var saved = await _admin.PutAsJsonAsync("/rest/v1/extensions/lyrics-languages/admin",
            new { values = new { readings = false, translation = "Llm", llmUrl = "http://127.0.0.1:9/v1", llmModel = "m", llmKey = "secret-key" } },
            ArcanistFactory.Json, Cancel);
        Assert.True((await saved.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel)).GetProperty("ok").GetBoolean());

        var stored = File.ReadAllText(Path.Combine(ExtensionData, "settings.json"));
        Assert.DoesNotContain("secret-key", stored);
        page = await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/extensions/lyrics-languages/admin?language=es", ArcanistFactory.Json, Cancel);
        Assert.DoesNotContain("secret-key", page.ToString());
        // Without readings there is nothing left to download.
        Assert.Equal(JsonValueKind.Null, page.GetProperty("setup").ValueKind);

        var bad = await _admin.PutAsJsonAsync("/rest/v1/extensions/lyrics-languages/admin", new { values = new { llmUrl = "ftp://nope" } }, ArcanistFactory.Json, Cancel);
        Assert.False((await bad.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_answer_with_its_lines_inside_other_text_is_read()
    {
        Assert.Equal(["uno", "", "tres"], LlmTranslator.Lines("Here you go:\n```json\n{\"lines\": [\"uno\", \"\", \"tres\"]}\n```"));
        Assert.Equal(["a", "b"], LlmTranslator.Lines("[\"a\", \"b\"]"));
        Assert.Null(LlmTranslator.Lines("Sorry, I cannot help with that."));
        await Task.CompletedTask;
    }
}
