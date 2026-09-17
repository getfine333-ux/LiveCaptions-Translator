# Local recognition models

Weights are downloaded separately and excluded from Git. The app does not need Python, CUDA, or a GPU for its current CPU recognition path.

The tested Chinese recognition setup uses the sherpa-onnx conversion `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17` with `model.onnx` (full precision), `tokens.txt`, and a separate `silero_vad.onnx`. Place the files under the application folder:

```text
models/
  silero_vad.onnx
  sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/
    model.onnx
    tokens.txt
    LICENSE
```

If both full precision and int8 weights exist, the current SenseVoice path prefers full precision. Do not change to int8 merely to reduce package size without testing recognition quality.

Sources: [sherpa-onnx SenseVoice documentation](https://k2-fsa.github.io/sherpa/onnx/sense-voice/index.html), [sherpa-onnx model releases](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models), [VAD documentation](https://k2-fsa.github.io/sherpa/onnx/vad/index.html).

Model weights have separate terms from this application's Apache-2.0 source license. The supplied conversion's LICENSE refers to FunASR model licensing. Preserve the license that accompanies the exact artifact you download and verify redistribution terms. See the [official SenseVoice model card](https://huggingface.co/FunAudioLLM/SenseVoiceSmall) and [upstream license clarification](https://github.com/QwenAudio/SenseVoice#license). The portable build script does not bundle or download model weights.
