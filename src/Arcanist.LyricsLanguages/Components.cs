using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Something the extension downloads when the administrator sets it up: the Japanese dictionary, the translation model,
/// or ONNX Runtime's native library from a NuGet package (only the files of the folder <paramref name="Extract"/>, and
/// with <paramref name="Only"/> only that one). Everything is checked against its SHA-256 before it is used.
/// </summary>
/// <param name="Target">Under the components folder: the file of a model, the folder of a package's files.</param>
public sealed record Component(string Id, string Name, string Url, string Sha256, long Size, string Target, string? Extract = null, string? Only = null);

/// <summary>What the extension needs, and downloading it.</summary>
public sealed class Components(ExtensionPaths paths, IHttpClientFactory http, ILogger<Components> logger)
{
    public const string HttpClientName = "lyrics-languages";

    private const string NuGet = "https://api.nuget.org/v3-flatcontainer/";
    private const string M2M100 = "https://huggingface.co/Xenova/m2m100_418M/resolve/9c374f0b7aca709787cea97b047bfbbd1559d177/";

    /// <summary>
    /// sherpa-onnx's runtime packages, the ones Lyrics Sync downloads: their ONNX Runtime (1.28) is the build both
    /// extensions load, so they never meet two in one process.
    /// </summary>
    private static readonly Dictionary<string, (string Sha256, long Size)> OnnxRuntimes = new()
    {
        ["win-x64"] = ("2257c06893d1d90792ea3157606de33a51122d935b6913bed5442d2ffb9e9bd7", 8535869),
        ["win-arm64"] = ("592463d28f93f6e0946acedfb98b140730448c54016f54e2df3ed382637d9b33", 8142361),
        ["linux-x64"] = ("65cf697ca95abfbd01dde3b015ede032880ac5fccff931aa6737cfe542c04e76", 10794309),
        ["linux-arm64"] = ("c1fd7628eddb84837f459c95bd9c41a8c3dc22ef1001b18f04f6c573c1d1d054", 13638800),
        ["osx-arm64"] = ("ce4c79d88f1a0aa0fe877f6acbfa4204842eeefc79750707b2fdd8b1e6a90bf8", 9824946),
        ["osx-x64"] = ("8b482b0d58ef651f1b97cd573112f95a0374a9db9bd010453030c999f757e14e", 11011142),
    };

    public static readonly Component Dictionary = new("ipadic", "Japanese dictionary (IPADIC)",
        NuGet + "mecab.dotnet/1.2.0/mecab.dotnet.1.2.0.nupkg", "4093cb6004d3b946feb0caec802d4019c6f43e9730b59823112e57f7bdecfd20", 14545606,
        "ipadic", "content/dic/");

    private static readonly Component[] Model =
    [
        new("m2m100-encoder", "M2M100 (encoder)", M2M100 + "onnx/encoder_model_quantized.onnx",
            "13a94e354a9140764eb81102d77d3ec6952d796e6f113c651eeb3c3443da0386", 287856370, "m2m100/encoder.onnx"),
        new("m2m100-decoder", "M2M100 (decoder)", M2M100 + "onnx/decoder_model_quantized.onnx",
            "6015e31c8976659aedb06058c4dadf0f400d087a3f9830f838e68f220d79bcb6", 339181945, "m2m100/decoder.onnx"),
        new("m2m100-sentencepiece", "M2M100 (vocabulary)", M2M100 + "sentencepiece.bpe.model",
            "d8f7c76ed2a5e0822be39f0a4f95a55eb19c78f4593ce609e2edbc2aea4d380a", 2423393, "m2m100/sentencepiece.bpe.model"),
        new("m2m100-vocab", "M2M100 (tokens)", M2M100 + "vocab.json",
            "b6e77e474aeea8f441363aca7614317c06381f3eacfe10fb9856d5081d1074cc", 3708092, "m2m100/vocab.json"),
    ];

    private readonly Lock _gate = new();
    private Task? _installing;

    /// <summary>This machine as native libraries name it (<c>linux-x64</c>…); null where there is none (32-bit, musl).</summary>
    public static string? Rid { get; } = DetectRid();

    public (string Name, long Done, long Total)? Progress { get; private set; }

    public string? LastError { get; private set; }

    public bool Installing
    {
        get
        {
            lock (_gate)
                return _installing is { IsCompleted: false };
        }
    }

    public string DictionaryFolder => Path.Combine(paths.Components, Dictionary.Target);

    public string ModelFolder => Path.Combine(paths.Components, "m2m100");

    public string OnnxRuntimeFolder => Path.Combine(paths.Components, "onnxruntime");

    /// <summary>Everything these settings need on this machine.</summary>
    public static IReadOnlyList<Component> Needed(LyricsLanguagesSettings settings)
    {
        var list = new List<Component>();
        if (settings.Readings)
            list.Add(Dictionary);
        if (settings.Translation == TranslationEngine.Local && Rid is { } rid)
        {
            var (sha, size) = OnnxRuntimes[rid];
            var library = rid.StartsWith("win", StringComparison.Ordinal) ? "onnxruntime.dll"
                : rid.StartsWith("osx", StringComparison.Ordinal) ? "libonnxruntime.dylib" : "libonnxruntime.so";
            list.Add(new Component("onnxruntime", "ONNX Runtime",
                NuGet + $"org.k2fsa.sherpa.onnx.runtime.{rid}/1.13.8/org.k2fsa.sherpa.onnx.runtime.{rid}.1.13.8.nupkg",
                sha, size, "onnxruntime", $"runtimes/{rid}/native/", library));
            list.AddRange(Model);
        }

        return list;
    }

    public bool IsInstalled(Component component) =>
        File.Exists(Marker(component)) && File.ReadAllText(Marker(component)).Trim() == component.Sha256;

    public IReadOnlyList<Component> Missing(LyricsLanguagesSettings settings) => Needed(settings).Where(c => !IsInstalled(c)).ToList();

    /// <summary>The local translation model and its runtime are here.</summary>
    public bool LocalModelReady => Rid is not null && Needed(LyricsLanguagesSettings.Default with { Readings = false, Translation = TranslationEngine.Local })
        .All(IsInstalled);

    /// <summary>Starts downloading what is missing, in the background; false when a download is already running.</summary>
    public bool StartInstall(LyricsLanguagesSettings settings, Action? done = null)
    {
        lock (_gate)
        {
            if (_installing is { IsCompleted: false })
                return false;
            LastError = null;
            _installing = Task.Run(async () =>
            {
                try
                {
                    foreach (var component in Missing(settings))
                        await InstallAsync(component, CancellationToken.None);
                }
                catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or TaskCanceledException
                                              or UnauthorizedAccessException)
                {
                    logger.LogWarning(e, "Could not download the components of Lyrics Languages");
                    LastError = e.Message;
                }
                finally
                {
                    Progress = null;
                    done?.Invoke();
                }
            });
            return true;
        }
    }

    /// <summary>Downloads one component, checks it and puts it in place.</summary>
    public async Task InstallAsync(Component component, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Components);
        var download = Path.Combine(paths.Components, $".download-{component.Id}");
        try
        {
            using (var client = http.CreateClient(HttpClientName))
            using (var response = await client.GetAsync(component.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? component.Size;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(download);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                Progress = (component.Name, 0, total);
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    Progress = (component.Name, done, total);
                }

                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != component.Sha256)
                    throw new InvalidDataException($"{component.Name}: the download does not match its SHA-256.");
            }

            var target = Path.Combine(paths.Components, component.Target);
            if (component.Extract is { } prefix)
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);
                using (var zip = ZipFile.OpenRead(download))
                {
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
                    {
                        // Only the files right in that folder: nothing can land outside the target.
                        if (entry.FullName[prefix.Length..].Contains('/') || component.Only is { } only && entry.Name != only)
                            continue;
                        entry.ExtractToFile(Path.Combine(target, entry.Name), overwrite: true);
                    }
                }

                File.Delete(download);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(download, target, overwrite: true);
            }

            File.WriteAllText(Marker(component), component.Sha256);
            logger.LogInformation("Lyrics Languages downloaded {Component}", component.Name);
        }
        finally
        {
            if (File.Exists(download))
                File.Delete(download);
        }
    }

    /// <summary>Removes what the settings no longer use (the local model when another engine translates).</summary>
    public void RemoveUnused(LyricsLanguagesSettings settings)
    {
        if (Installing)
            return;
        var needed = Needed(settings).Select(c => c.Id).ToHashSet();
        if (!needed.Contains("m2m100-encoder"))
        {
            foreach (var component in Model)
                DeleteMarker(component.Id);
            TryDelete(ModelFolder);
        }

        if (!needed.Contains(Dictionary.Id))
        {
            DeleteMarker(Dictionary.Id);
            TryDelete(DictionaryFolder);
        }
    }

    private void DeleteMarker(string id)
    {
        var marker = Path.Combine(paths.Components, $".{id}.sha256");
        if (File.Exists(marker))
            File.Delete(marker);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // In use until the next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Marker(Component component) => Path.Combine(paths.Components, $".{component.Id}.sha256");

    private static string? DetectRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() && !RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) ? "linux"
            : null;
        return arch is null || os is null ? null : $"{os}-{arch}";
    }
}
