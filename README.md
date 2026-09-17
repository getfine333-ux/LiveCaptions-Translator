# LiveCaptions Translator

[中文](README_zh-CN.md) | English

A Windows desktop speech translator with local speech recognition, stable bilingual captions and an editable glossary. This is a modified derivative of [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator), not an official upstream release.

## Features

- Capture system playback or a microphone, with CPU-based local recognition through sherpa-onnx.
- Preserve ordered segments while translating concurrently; retry failures without replacing later sentences.
- Show complete sentences or clauses with protected reading time, pause/next controls and recent captions at the top.
- Edit terminology from the home or settings page; optionally generate a draft glossary using your configured AI API.
- Apply settings only after clicking **Save and apply**.
- Keep translation history and local diagnostic records for troubleshooting.

Recognition and translation can still make mistakes. Neither perfect coverage nor fixed latency is guaranteed. The older Windows Live Captions and cloud ASR integrations remain available, but the primary validation target is local SenseVoice with an OpenAI-compatible translation API.

## Run on Windows

The supported release target is **Windows 11 x64**. No discrete GPU, CUDA or Python is needed for CPU recognition. A modern four-core CPU and 8 GB RAM are an initial trial recommendation; 16 GB is preferable when playing browser videos. This is not a measured minimum specification.

1. Obtain this fork's complete portable ZIP from its releases or CI artifacts and extract it into a writable folder. Do not use an upstream single EXE for this fork.
2. Download recognition models separately, following [models/README.md](models/README.md). Weights are not included in the source or portable ZIP.
3. Run `start.bat` from the extracted folder.
4. Configure a translation endpoint, model and your own API key in Settings. Click **Save and apply**. The example uses DeepSeek's OpenAI-compatible endpoint; no service subscription is included.
5. Select system audio or microphone. Use the glossary button to add domain terminology before playback.

Portable builds include the .NET runtime. The current package plus full-precision model needs roughly 2 GB free space, with additional space for history. ARM64, Windows 10, macOS and Linux are not supported release targets at present.

## Build and test

Install the .NET 8 SDK and PowerShell 7 on Windows, then run from the repository root:

```powershell
dotnet restore tests/PipelineTests.csproj
dotnet run --project tests/PipelineTests.csproj -c Release -- --ci
pwsh ./package-portable.ps1
```

The default regression suite uses generated inputs, mocked API responses and WPF UI tests. It does not capture audio, call paid providers or require private recordings/model downloads. Native integration checks require separately obtained models and fixtures and are explicitly enabled with `--integration`; see [CONTRIBUTING.md](CONTRIBUTING.md).

Packages appear under `artifacts/portable/`. [README_Portability.md](README_Portability.md) describes deployment and platform limits. .NET 8 is the current build target; migration to a supported successor needs separate validation before its support ends ([Microsoft lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)).

## Privacy

Local recognition keeps audio on the machine. Online translation sends recognized text and relevant context/terminology to the configured provider. Cloud ASR sends audio to that provider. Glossary generation sends the entered domain description and prompt to the configured AI endpoint and can incur charges.

Settings, history, transcripts, diagnostics and recovery/spool files can contain sensitive data. Keys entered directly are stored in the local configuration; `${VARIABLE_NAME}` references can instead resolve environment variables. The app does **not** automatically read `.env`. See [PRIVACY.md](PRIVACY.md) before sharing logs or screenshots.

Automatic upstream update checks are disabled for this fork. The legacy `Google2` selection now uses the existing `Google` path; no browser-extension API credential is redistributed. Third-party service availability is not guaranteed.

## License and provenance

Application source is distributed under [Apache-2.0](LICENSE). Original attribution is retained in [NOTICE](NOTICE); [CHANGES.md](CHANGES.md) records this derivative's changes. Dependency notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses/`. Model weights have separate terms.

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [OPEN_SOURCE_RELEASE.md](OPEN_SOURCE_RELEASE.md) for development and publication instructions. Report bugs using a minimal synthetic example; do not attach your configuration, real recordings or credentials.
