# Third-party notices

## Shipped in the package

| Component | License | Use |
|---|---|---|
| [MeCab.DotNet](https://github.com/kekyo/MeCab.DotNet) 1.2.0 (a port of [NMeCab](https://github.com/komutan/NMeCab), from [MeCab](https://taku910.github.io/mecab/)) | LGPL-2.1 (offered under GPL-2.0 or LGPL-2.1; used under LGPL-2.1) | Splits Japanese into words with their readings. |
| [ICU4N](https://github.com/NightOwl888/ICU4N) and ICU4N.Transliterator 60.1.0-alpha.356, with ICU's data | Apache-2.0 (ICU's data: Unicode License) | Chinese in pinyin and other scripts in the Latin alphabet. |
| [J2N](https://github.com/NightOwl888/J2N) | Apache-2.0 | A dependency of ICU4N. |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) 1.28.0 (`Microsoft.ML.OnnxRuntime.Managed`, managed part only) | MIT | Runs the local translation model. |
| [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning) 2.0.1 | MIT | The local model's SentencePiece tokens. |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) | BSD-3-Clause | A dependency of Microsoft.ML.Tokenizers. |

## Downloaded when the administrator sets the extension up

Checked against the SHA-256 written in `Components.cs` before use:

| Component | License | From |
|---|---|---|
| IPADIC, the Japanese dictionary (from the `MeCab.DotNet` 1.2.0 package) | NAIST-2003 (permissive; ICOT Free Software conditions) | nuget.org |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) 1.28 native library (from `org.k2fsa.sherpa.onnx.runtime.<platform>` 1.13.8, the build Lyrics Sync uses; only `onnxruntime` is extracted) | MIT | nuget.org |
| [M2M100](https://huggingface.co/facebook/m2m100_418M) 418M by Meta, exported to ONNX and quantized by the Transformers.js project (`encoder_model_quantized.onnx`, `decoder_model_quantized.onnx`, `sentencepiece.bpe.model`, `vocab.json`) | MIT | huggingface.co/Xenova/m2m100_418M |

Only when the local model is chosen (the dictionary when pronunciation is on).

Not used, for their licenses: NLLB-200 (CC BY-NC 4.0).

## Services

With an LLM chosen, the lines of the lyrics to translate are sent to the address the administrator gives (an Ollama or
LM Studio on their network, or a provider with an API like OpenAI's, under that provider's terms).
