# 发布与升级 · Windows

## 核心摘要

- 交付对象：全新 Windows 普通用户电脑。
- 发布包：自包含应用、正式启动脚本、数据库迁移、固定版只读 Reader runtime 和 Agent 安装手册。
- 目标机依赖：Windows 10/11 x64、winget/App Installer、PostgreSQL 18、64 位 Python 3.10+（3.11 推荐）；缺少 PostgreSQL/Python 时由 Setup 通过 winget 安装 PostgreSQL 18 和推荐版 Python 3.11。
- 产品内置 AI Agent：不随包携带 Codex CLI 凭据。需在目标机安装 Codex CLI 并由使用者登录；这与负责安装的操作型 Agent 是两回事。
- 安全边界：包未签名；接收者需从可信维护者取得 zip 与 SHA-256。发布包不包含用户数据库和任何凭据。
- 交付入口：[Agent_Install_Runbook.md](Agent_Install_Runbook.md) 随包内附并在 release 目录单独提供；新 Agent 需同时拿到四个文件。

## 1. 维护者：构建并交付

在正式 product 目录运行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Build-ReleasePackage.ps1
~~~

构建机需要 .NET SDK、64 位 Python 3.10+（优先使用 3.11）、NuGet 源和 PyPI 网络访问；dotnet publish 会从 NuGet 还原应用依赖，Reader runtime 缺失时 prepare-reader.ps1 会按当前 Python ABI 从 PyPI 下载依赖并导入自检。任一依赖源不可达或恢复失败，构建应中止，不得交付缺依赖的包。

产出在 artifacts/release/，交付给新机器必须四个文件：

| 文件 | 用途 |
|---|---|
| WeChatAiCopilot-<版本>-win-x64.zip | 自包含应用、启动/停止/安装脚本、PostgreSQL 迁移、固定版 Reader runtime 和 Agent 安装手册 |
| 同名 .zip.sha256 | 校验压缩包 |
| Install-FromPackage.ps1 | 包外安装入口 |
| Agent_Install_Runbook.md | 可直接交给新环境操作 Agent 的自包含安装与验收步骤 |

构建过程会生成 release-manifest.json 和 SHA256SUMS.txt，并在压缩包内核对应用入口、Reader runtime 和 Agent 手册。接收者安装时会按清单逐文件复核。确保四个外部交付文件来自同一次构建，不要把不同版本的 zip、sha256、安装器或手册混在一起。

当前包没有代码签名。Windows SmartScreen 可能提示未知发布者；不要指导接收者关闭安全功能。若未通过可信渠道把 SHA-256 交给接收者，不要发布。

## 2. 接收者：全新电脑安装

将四个交付文件放在同一目录，并将四个文件都交给操作型 AI Agent。按 Runbook 执行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./Install-FromPackage.ps1 -PackageZip "./WeChatAiCopilot-<版本>-win-x64.zip" -RunSetup
~~~

安装器先核对外部 SHA-256，再解压临时目录并逐项校验清单，最后镜像到当前用户的 %LOCALAPPDATA%/Programs/WeChatAiCopilot。它会检查程序目录里没有本产品正在运行的进程；若已运行，应先通过“知间 停止”关闭。

Setup 会检查 64 位 Windows、PostgreSQL 18、可运行 Reader runtime 的 64 位 Python 3.10+ 和桌面快捷方式。3.11 是推荐版本，不是唯一可接受版本；已安装的其他版本先通过 ABI 导入自检，失败时尝试按该版本从 PyPI 重建依赖。没有 Python 时需 winget 与可用软件源；正常情况下接收机不需要 PyPI。接收机不需要 .NET SDK。PostgreSQL 机器范围安装若弹出 UAC，需由使用者本人批准；没有管理员权限或未批准则不能完成全新安装。若 winget 不存在、其他安装步骤要求提权、哈希不一致或包内文件缺失，停止并报告，不得跳过校验。

聊天数据库、密钥与运行状态不在安装目录，而在 %LOCALAPPDATA%/WeChatAiCopilot。安装器不会镜像或清理该用户数据目录。若目录已存在，应按升级或沿用旧数据处理，不可当作干净安装。

## 3. 产品内置 AI Agent 部署

正式启动器固定设置产品 Agent 为 Codex CLI runtime。因此：

- 本机工作区与 Reader 可在 Codex CLI 缺失时启动；
- 生成建议/咨询只有在当前 Windows 用户能运行 Codex CLI 且登录有效时才算就绪；
- release 包不包含 Codex CLI、模型账户或认证文件。

目标机没有 Codex CLI 时，使用者同意后按[OpenAI 官方 Windows 安装说明](https://github.com/openai/codex#quickstart)安装。程序不固定 Codex CLI 版本号；在新开的普通 PowerShell 中查看 codex --version（诊断信息）和 codex login status，未登录则由使用者运行 codex login 并自行完成浏览器登录。最终以一次实际 Agent 请求是否通过协议和登录检查为准。不要收集 API key、token、auth.json 或浏览器回调。

产品用户明确请求建议/咨询后，必要的获准会话上下文会交给已登录的 Codex CLI/OpenAI 服务。部署 Agent 不得为了“证明安装成功”而自动发送真实会话上下文。只有使用者明确同意后才做一次 AI 冒烟；否则分别报告“本机工作区与 Reader 已就绪”和“AI Agent 未验证”。

## 4. 升级与回滚

升级前双击“知间 停止”，再用同样的四文件流程安装新包。安装器只镜像更新程序目录，不触碰 Formal 数据目录。

首次启动发现新数据库迁移时，会自动完整备份、还原到临时库并比对行数；恢复演练通过后才应用迁移。失败时启动会中止并保留原库。备份保留最近 10 份在 Formal/backups。

- 应用回滚：保留上一版完整发布四文件，重新安装上一版 zip；不要用新版本的 sha256 或安装器配旧包。
- 数据库回滚：从升级前备份恢复到新数据库，再将服务指向恢复库。当前没有自动反向迁移脚本。
- 手动备份/恢复演练：运行 scripts/Backup-FormData.ps1 -Verify -Keep 10。备份含聊天正文和凭据，按敏感个人数据保护。

## 5. 发布前的全新环境验收门槛

构建脚本的压缩包自检不等于全新电脑冒烟。公开宣称“新电脑可一键部署”前，维护者还必须在隔离的 Windows x64 干净环境按接收者流程验收：

1. 初始环境无 PostgreSQL、无 Python、无 .NET SDK；保留 winget/App Installer。
2. 放入同一次构建的四个文件，运行完整安装命令；确认 SHA-256、清单、winget 安装、Reader runtime 版本及快捷方式。
3. 登录微信，由使用者选择一个 Direct 联系人，确认 API ready、工作区打开、Reader 监听中。
4. 若要宣称产品内置 AI Agent 已部署，另行安装并登录 Codex CLI；只有取得用户同意后，才做一次真实 AI 请求。
5. 停止程序，确认 5080 和 55432 不再监听。

当前还没有在全新 Windows VM 上完成上述完整冒烟。真实微信版本、用户授权、Codex 登录和 AI 请求都需要接收者本人参与；这些条件没验收前不能宣称整条部署已在干净环境全通过。

## 6. 当前发行限制

- 没有自动更新；每次升级需重新交付完整四文件。
- 压缩包不是字节级可复现构建；可核验的是清单内各文件 SHA-256 和外部 zip SHA-256。
- 安装包和脚本未代码签名。当前仅适合通过可信渠道交付并核对哈希的本机安装。
