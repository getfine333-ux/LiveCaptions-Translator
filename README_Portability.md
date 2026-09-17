# 部署、配置与平台支持

## 便携运行

发布目标为 Windows 11 x64。便携 ZIP 自带 .NET 与 Windows Desktop 运行时，并保留识别和 SQLite 原生库等侧边文件；只复制 EXE 不够。解压到可写目录后按 [models/README.md](models/README.md) 放入模型，再运行 `start.bat`。

模型、术语表、记录目录默认使用相对路径。设置自己的 API Key 后点击“保存并应用”。可在 `setting.json` 使用 `${DEEPSEEK_API_KEY}` 引用 Windows 用户/进程环境变量，配置变量后需重启程序。程序不会自动读取 `.env` 文件。

完整精度 SenseVoice 优先于 int8；默认 4 个 CPU 线程。不需要独显、NPU、Python 或 CUDA。在线翻译需要网络及自己的 API 配额；本地大语言模型翻译的硬件需求需要单独评估。

## 硬件与平台边界

- 初步建议现代四核 CPU、8 GB 内存；播放网页视频时建议 16 GB。不是已验证的最低配置。
- 程序与完整精度识别模型建议预留约 2 GB 磁盘，记录会额外占用空间。
- Windows 10、ARM64 尚未验收，CI 只构建 win-x64。
- macOS/Linux 不能直接运行当前 WPF 界面。跨平台迁移还需替换 WASAPI 音频捕获与 Windows UI Automation。[WPF 平台说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- 纯净机器、不同声卡和低功耗电脑需要实机测试；构建通过不能替代这些验证。

## 开发与打包

```powershell
dotnet restore tests/PipelineTests.csproj
dotnet run --project tests/PipelineTests.csproj -c Release -- --ci
pwsh ./package-portable.ps1
```

输出在 `artifacts/portable/<构建标识>/`。脚本只复制公开配置模板、示例术语和许可文件，不复制当前个人配置、密钥、转录或模型。使用 `-NoRestore` 前需已还原 win-x64/self-contained 依赖。

当前目标为 .NET 8；长期维护应在其支持结束前单独迁移并验证后续 LTS 版本，参见 [Microsoft 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。

## 首次使用与排错

模型列表空白时，检查 `tokens.txt`、`model.onnx` 与目录结构；没有 VAD 文件时补齐 `models/silero_vad.onnx`。模型不随便携包自动下载。

有原文而无译文时，检查所选服务的 URL、模型和自己的 API Key。测试连接与实际翻译可能计费。不要把包含密钥的错误信息或配置直接贴到公开 Issue。

隐私数据的位置与处理方式见 [PRIVACY.md](PRIVACY.md)。许可、公开源码导出与发布步骤见 [OPEN_SOURCE_RELEASE.md](OPEN_SOURCE_RELEASE.md)。
