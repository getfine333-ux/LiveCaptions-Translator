# LiveCaptions Translator

中文 | [English](README.md)

Windows 实时语音翻译工具，支持本地识别、稳定的双语字幕、可编辑术语表。本项目基于 [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator) 修改，属于独立衍生版本。

## 功能

- 识别系统播放声音或麦克风输入，通过 sherpa-onnx 在 CPU 上运行本地模型。
- 并发翻译、按顺序展示，失败重试保留原段落关联。
- 按完整句子或分句展示，提供阅读停留、下一段、跳到最新和历史字幕。
- 从主页或设置页编辑术语表，也可描述领域后调用已配置的 AI API 生成草稿，检查后保存。
- 设置修改按“保存并应用”后才生效。
- 本地保存翻译历史及诊断记录，便于排查问题。

语音识别和翻译仍可能出现错字、漏识别或延迟，不保证所有语速与内容下完全准确。主要验证组合为本地 SenseVoice 与 OpenAI 兼容翻译接口；保留 Windows Live Captions 与云端识别入口。

## 安装与使用

发布目标为 **Windows 11 x64**。本地识别不需要独显、CUDA 或 Python。初步建议现代四核 CPU、8 GB 内存；同时播放网页视频时建议 16 GB。这不是经过全面验证的最低配置。

1. 从本分支的 Releases 或 CI 构建产物获取完整便携 ZIP，解压到可写目录。
2. 按 [模型说明](models/README.md) 单独下载 SenseVoice、tokens 和 Silero VAD。源码与便携包均不包含模型权重。
3. 双击解压目录内的 `start.bat`。
4. 设置自己的翻译服务地址、模型及 API Key，点击“保存并应用”。示例为 DeepSeek 的 OpenAI 兼容接口，不附带服务额度。
5. 选择系统音频或麦克风。开始前可通过“术语表”入口补充领域词汇。

便携包自带 .NET 运行时，程序加当前完整精度模型建议预留约 2 GB 磁盘空间，并为字幕记录留出余量。ARM64、Windows 10、macOS 和 Linux 尚未作为发布目标验收。详细说明见 [部署与配置](README_Portability.md)。

## 从源码构建

在 Windows 安装 .NET 8 SDK 与 PowerShell 7，然后在仓库根目录执行：

```powershell
dotnet restore tests/PipelineTests.csproj
dotnet run --project tests/PipelineTests.csproj -c Release -- --ci
pwsh ./package-portable.ps1
```

默认测试使用合成输入、模拟接口和 WPF 界面测试，不采集声音、不调用付费 API，也不依赖个人录音或下载模型。`--integration` 为需要额外本地夹具的集成验证，见 [贡献说明](CONTRIBUTING.md)。生成的便携包位于 `artifacts/portable/`。

## 隐私与注意事项

本地识别在电脑上处理音频。在线翻译会向已配置服务发送识别文本及相关上下文、术语；云端识别会发送音频；AI 生成术语表会发送输入的领域描述及提示词，并可能产生 API 费用。

配置、历史数据库、转录、诊断日志和恢复文件可能含敏感信息。直接填写的密钥保存在本地配置中，也可用 `${变量名}` 引用环境变量；程序不会自动加载 `.env`。请阅读 [隐私说明](PRIVACY.md)，不要直接上传配置、日志、截图或真实录音。

本分支已关闭自动检查上游更新，避免错误切换到原项目版本。兼容旧配置的 `Google2` 入口改为使用已有 `Google` 路径，不再分发上游浏览器扩展的内置密钥。第三方接口可用性取决于服务提供方。

## 开源许可

应用源码使用 [Apache-2.0](LICENSE)，保留原作者及贡献者归属，见 [NOTICE](NOTICE)。修改说明见 [CHANGES.md](CHANGES.md)。依赖许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 `licenses/`；模型权重另行遵守对应许可。

参与开发见 [CONTRIBUTING.md](CONTRIBUTING.md)，公开发布见 [OPEN_SOURCE_RELEASE.md](OPEN_SOURCE_RELEASE.md)。问题反馈请尽量提供不含个人内容的最小复现。
