using Arcanist.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Lyrics Languages for Arcanist: how lyrics in scripts many cannot read are pronounced (Japanese in romaji with
/// furigana, Chinese in pinyin, Korean romanized, other scripts in the Latin alphabet), and lyrics translated on request
/// with a local model or the administrator's LLM. Players get both through Arcanist's lyrics API.
/// </summary>
public sealed class LyricsLanguagesExtension : IArcanistExtension
{
    public const string Id = "lyrics-languages";

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(new ExtensionPaths(context.DataDirectory));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<Components>();
        services.AddSingleton<LyricsReadings>();
        services.AddSingleton<ILyricsReadings>(provider => provider.GetRequiredService<LyricsReadings>());
        services.AddSingleton<LlmTranslator>();
        services.AddSingleton<LocalTranslator>();
        services.AddSingleton<LyricsTranslator>();
        services.AddSingleton<ILyricsTranslator>(provider => provider.GetRequiredService<LyricsTranslator>());
        services.AddKeyedScoped<IExtensionAdminPage, LyricsLanguagesAdminPage>(context.Id);
        var version = typeof(LyricsLanguagesExtension).Assembly.GetName().Version?.ToString(3) ?? "0";
        services.AddHttpClient(Components.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Arcanist-LyricsLanguages/" + version);
            client.Timeout = TimeSpan.FromMinutes(30);
        });
        services.AddHttpClient(LlmTranslator.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Arcanist-LyricsLanguages/" + version);
            // A local LLM on a small machine can take minutes over a whole song.
            client.Timeout = TimeSpan.FromMinutes(10);
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ExtensionContext context)
    {
        endpoints.MapGet(context.BasePath + "/icon.svg", () =>
        {
            var stream = typeof(LyricsLanguagesExtension).Assembly.GetManifestResourceStream("icon.svg")!;
            return Results.Stream(stream, "image/svg+xml");
        });
    }
}
