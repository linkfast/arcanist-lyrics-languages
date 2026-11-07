using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Arcanist.LyricsLanguages;

/// <summary>
/// Translates on this server with M2M100 (418M parameters, quantized ONNX; MIT): 100 languages to and from each other,
/// line by line, choosing the likeliest word at each step. Literal more than poetic, but private and offline. The model
/// is loaded when there is something to translate and let go after ten idle minutes (it takes about a gigabyte).
/// </summary>
public sealed class LocalTranslator(Components components, ILogger<LocalTranslator> logger) : IDisposable
{
    /// <summary>M2M100's languages, in the order of its language tokens (they follow its vocabulary).</summary>
    private static readonly string[] ModelLanguages =
    [
        "af", "am", "ar", "ast", "az", "ba", "be", "bg", "bn", "br", "bs", "ca", "ceb", "cs", "cy", "da", "de", "el", "en", "es", "et", "fa", "ff", "fi",
        "fr", "fy", "ga", "gd", "gl", "gu", "ha", "he", "hi", "hr", "ht", "hu", "hy", "id", "ig", "ilo", "is", "it", "ja", "jv", "ka", "kk", "km", "kn",
        "ko", "lb", "lg", "ln", "lo", "lt", "lv", "mg", "mk", "ml", "mn", "mr", "ms", "my", "ne", "nl", "no", "ns", "oc", "or", "pa", "pl", "ps", "pt",
        "ro", "ru", "sd", "si", "sk", "sl", "so", "sq", "sr", "ss", "su", "sv", "sw", "ta", "th", "tl", "tn", "tr", "uk", "ur", "uz", "vi", "wo", "xh",
        "yi", "yo", "zh", "zu",
    ];

    private const long EndOfSentence = 2;
    private const long Unknown = 3;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private static string? _nativeFolder;
    private static bool _resolverSet;

    private readonly Lock _gate = new();
    private Model? _model;
    private Timer? _unload;

    public static bool Supports(string language) => Array.IndexOf(ModelLanguages, Primary(language)) >= 0;

    private static string Primary(string language) => language.Split('-')[0].ToLowerInvariant() switch
    {
        "iw" => "he",
        "nb" or "nn" => "no",
        var code => code,
    };

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, string? from, string to, int threads, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            lock (_gate)
            {
                var model = Load(threads);
                var source = from is not null && Supports(from) ? Primary(from) : "en";
                var result = new List<string>(lines.Count);
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(string.IsNullOrWhiteSpace(line) ? "" : model.Translate(line, source, Primary(to), cancellationToken));
                }

                _unload?.Change(Idle, Timeout.InfiniteTimeSpan);
                return result;
            }
        }, cancellationToken);

    private Model Load(int threads)
    {
        if (_model is not null)
            return _model;
        if (!components.LocalModelReady)
            throw new InvalidOperationException("The local translation model is not downloaded.");
        UseNativeLibrary(components.OnnxRuntimeFolder);
        _model = new Model(components.ModelFolder, threads);
        _unload ??= new Timer(_ => Unload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        logger.LogInformation("Loaded the local translation model");
        return _model;
    }

    private void Unload()
    {
        lock (_gate)
        {
            _model?.Dispose();
            _model = null;
        }
    }

    /// <summary>ONNX Runtime's native library from the components (the package ships only its managed part).</summary>
    private static void UseNativeLibrary(string folder)
    {
        _nativeFolder = folder;
        if (_resolverSet)
            return;
        _resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (_nativeFolder is null || !name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        var file = OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";
        var full = Path.Combine(_nativeFolder, file);
        return File.Exists(full) ? NativeLibrary.Load(full) : IntPtr.Zero;
    }

    public void Dispose()
    {
        _unload?.Dispose();
        Unload();
    }

    private sealed class Model : IDisposable
    {
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly SentencePieceTokenizer _pieces;
        private readonly Dictionary<string, long> _vocabulary;
        private readonly Dictionary<long, string> _tokens;

        public Model(string folder, int threads)
        {
            var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
            _encoder = new InferenceSession(Path.Combine(folder, "encoder.onnx"), options);
            _decoder = new InferenceSession(Path.Combine(folder, "decoder.onnx"), options);
            using var model = File.OpenRead(Path.Combine(folder, "sentencepiece.bpe.model"));
            _pieces = SentencePieceTokenizer.Create(model, false, false);
            _vocabulary = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Path.Combine(folder, "vocab.json")))!;
            _tokens = _vocabulary.ToDictionary(p => p.Value, p => p.Key);
        }

        private long LanguageToken(string language) => _vocabulary.Count + Array.IndexOf(ModelLanguages, language);

        public string Translate(string text, string from, string to, CancellationToken cancellationToken)
        {
            var ids = new List<long> { LanguageToken(from) };
            ids.AddRange(_pieces.EncodeToTokens(text, out _).Select(t => _vocabulary.TryGetValue(t.Value, out var id) ? id : Unknown));
            ids.Add(EndOfSentence);
            var length = ids.Count;
            var input = new DenseTensor<long>(ids.ToArray(), [1, length]);
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length]);
            using var encoded = _encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", input), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
            var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

            // The decoder starts with </s> and the language to write in, then takes the likeliest token until </s>.
            var output = new List<long> { EndOfSentence, LanguageToken(to) };
            var limit = Math.Min(256, length * 3 + 16);
            while (output.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoderInput = new DenseTensor<long>(output.ToArray(), [1, output.Count]);
                using var result = _decoder.Run([
                    NamedOnnxValue.CreateFromTensor("input_ids", decoderInput),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden)]);
                var logits = result.First().AsTensor<float>();
                var size = logits.Dimensions[2];
                var last = output.Count - 1;
                long best = 0;
                var bestScore = float.MinValue;
                for (var token = 0; token < size; token++)
                {
                    var score = logits[0, last, token];
                    if (score > bestScore)
                        (bestScore, best) = (score, token);
                }

                if (best == EndOfSentence)
                    break;
                output.Add(best);
            }

            return string.Concat(output.Skip(2).Select(id => _tokens.GetValueOrDefault(id, ""))).Replace('▁', ' ').Trim();
        }

        public void Dispose()
        {
            _encoder.Dispose();
            _decoder.Dispose();
        }
    }
}
