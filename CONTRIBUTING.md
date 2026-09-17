# Contributing

Use Windows 11 x64, .NET 8 SDK and PowerShell 7 for app development. Python 3 and Gitleaks are used only for source-release checks, not by the installed application.

```powershell
dotnet restore tests/PipelineTests.csproj
dotnet run --project tests/PipelineTests.csproj -c Release -- --ci
pwsh ./package-portable.ps1
```

The default test executable is a custom regression harness, not an xUnit project. Use `dotnet run`, not a successful empty `dotnet test`, as evidence. Results are written under the ignored `artifacts/pipeline-tests/` directory. WPF checks need a Windows desktop environment. Tests create synthetic configuration and mocked API replies; they never require a real key or capture device.

`--integration` additionally enables local model/fixture and old-binary comparisons. Those fixtures are intentionally not distributed. Missing prerequisites can skip or fail an integration check; do not describe the offline suite as a real-audio quality measurement. Native tests require the model layout described in `models/README.md`, including the upstream `test_wavs/zh.wav` sample. CLI modes `--capture`, `--provider-smoke` and `--provider-number-smoke` are explicit manual tools; the last two contact the user's configured service and may incur charges. Never run them in CI.

Keep PRs focused. Preserve speech IDs, source revisions, retained reading time and original audio ownership when changing the pipeline. Use generated text/audio for regressions, and explain validation limits.

Before contributing, inspect `git diff --cached` and use secret scanning. Never force-add ignored configuration, diagnostics, transcripts, database files or recordings. Use a GitHub noreply commit address if you do not want a personal email in public history. Configure identity in the publication repository rather than changing a shared/global identity.

See `OPEN_SOURCE_RELEASE.md` for the public-source export and release workflow. Source remains Apache-2.0 with original attribution; new dependencies or model distributions require their own notice review.
