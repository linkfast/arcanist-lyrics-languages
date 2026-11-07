using Microsoft.Extensions.Logging.Abstractions;

namespace Arcanist.LyricsLanguages.Tests;

/// <summary>
/// The local model translating for real. Needs the model (ARCANIST_M2M100: a folder with encoder.onnx, decoder.onnx,
/// sentencepiece.bpe.model and vocab.json) and ONNX Runtime's native library (ARCANIST_ONNXRUNTIME: its folder);
/// skipped without them, as they weigh hundreds of megabytes.
/// </summary>
public sealed class LocalTranslatorTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_local_model_translates_each_line_and_keeps_empty_ones()
    {
        var model = Environment.GetEnvironmentVariable("ARCANIST_M2M100");
        var runtime = Environment.GetEnvironmentVariable("ARCANIST_ONNXRUNTIME");
        Assert.SkipWhen(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(runtime), "ARCANIST_M2M100 and ARCANIST_ONNXRUNTIME are not set");

        // The components as set-up leaves them.
        var paths = new ExtensionPaths(Directory.CreateTempSubdirectory("arcanist-m2m100-").FullName);
        var components = new Components(paths, null!, NullLogger<Components>.Instance);
        Directory.CreateDirectory(components.ModelFolder);
        Directory.CreateDirectory(components.OnnxRuntimeFolder);
        foreach (var file in Directory.GetFiles(model!))
            File.Copy(file, Path.Combine(components.ModelFolder, Path.GetFileName(file)));
        foreach (var file in Directory.GetFiles(runtime!))
            File.Copy(file, Path.Combine(components.OnnxRuntimeFolder, Path.GetFileName(file)));
        foreach (var component in Components.Needed(LyricsLanguagesSettings.Default with { Readings = false, Translation = TranslationEngine.Local }))
            File.WriteAllText(Path.Combine(paths.Components, $".{component.Id}.sha256"), component.Sha256);
        Assert.True(components.LocalModelReady);

        using var translator = new LocalTranslator(components, NullLogger<LocalTranslator>.Instance);
        var lines = await translator.TranslateAsync(["月亮代表我的心", "", "I love you"], "zh", "es", 2, Cancel);

        Assert.Equal(3, lines.Count);
        Assert.Contains("luna", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", lines[1]);
        Assert.Contains("quiero", lines[2], StringComparison.OrdinalIgnoreCase);
    }
}
