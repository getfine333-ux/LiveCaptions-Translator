# 发布本分支

## 公开源码

安装 Python 3、PowerShell 7 与 [Gitleaks](https://github.com/gitleaks/gitleaks)，在开发目录运行：

```powershell
pwsh ./prepare-open-source.ps1
```

脚本按 `public-source.json` 中的明确清单导出新的源码目录，并运行内容检查及 Gitleaks。只打包源码、公开文档、模板、合成测试和许可文件；不会复制 `.git`、本机配置、历史记录、模型或原始截图。输出的源码 ZIP 可以用于建立新的 GitHub 仓库。

**请从导出的目录建立公开仓库，不要直接推送原开发仓库或复制整个工作目录。** `.gitignore` 不能清除旧提交的敏感内容。源码快照保留 LICENSE、NOTICE、上游引用与修改说明，不携带原开发仓库的作者邮箱、远程地址或旧凭据历史。

首次初始化前确定公开仓库名称和 GitHub noreply 提交邮箱；仅在新的发布目录设置 Git 身份。不要把个人邮箱、用户名目录或带认证令牌的远程 URL 写入文档与脚本。导出脚本不会提交、创建远程仓库或推送。

## 本地验收

在导出的源码目录执行 README 的构建与 `--ci` 测试命令，再运行 `package-portable.ps1`。该步骤可确认没有依赖开发机器上的私人夹具。真实录音与网络翻译的端到端测试另行进行，不上传样本或 API 响应。

## GitHub Actions 与版本包

- PR、分支推送和手动触发会执行公开源码检查、离线测试及 Windows x64 打包。
- 工作流只上传源码 ZIP、便携 ZIP 与校验和，不上传测试目录或日志。
- `v*` 标签触发 **草稿** Release，附完整 ZIP 和 SHA256；维护者查看后再公开发布。
- 默认权限为只读，只有草稿发布任务有 `contents: write`；不使用 `pull_request_target`，PR 不需要 API 密钥。
- 当前自动检查上游版本已停用；确定本分支地址并实现对应校验后才能启用本分支更新检查。
- 用户机器上仍需验证声卡、纯净系统启动与模型安装体验；本地模拟 CI 不能替代 GitHub runner 的实际运行。

## 许可与内容

保持 `LICENSE` 和 `NOTICE`，修改文件中的归属不得删除。更新依赖时同步核对 `THIRD_PARTY_NOTICES.md` 与 `licenses/`；自带 .NET 运行时的许可由打包脚本保留。模型权重不随源码或便携包分发。

不要把本地文档、个人截图、原始字幕、设备信息或真实 API Key 添加到 `public-source.json`。若要提供演示图，请另外制作完全合成的示例。
