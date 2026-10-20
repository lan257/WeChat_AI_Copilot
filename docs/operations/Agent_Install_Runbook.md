# 全新 Windows 环境部署手册（交给本机操作 Agent）

**适用对象**：在目标电脑上能读取交付附件、运行当前用户 PowerShell、检查浏览器结果的 Qoder、Codex、Claude 等操作型 Agent。
**目标**：只使用可信发布包，在全新 Windows 用户环境完成安装、首次授权与可复核验收；由 Agent 执行命令，不要求使用者复制粘贴。

## 部署摘要

- 做什么：核对发布包，安装应用、PostgreSQL、Python 和随包提供的只读 Reader runtime。
- 最终效果：本机 API、私有数据库、一个获准微信账号及至少一个 Direct 会话可用。
- 主要影响：首次启动默认把所选会话最近最多 5,000 条可读文本消息补录到本机私有数据库；产品内置 AI 建议需要另行安装并登录 Codex CLI。
- 数据边界：部署日志和 Agent 报告不得含消息正文、令牌或 wxid；用户明确请求 AI 建议时，必要会话上下文会交给用户登录的 Codex CLI/OpenAI 服务处理。
- 决策点：使用者本人确认微信登录、选择联系人、决定是否在本机补录历史；如 winget 安装 PostgreSQL 触发 UAC，由使用者批准；产品内置 AI 另需 Codex 登录与 AI 数据使用同意。
- 验收入口：第 6 节验收本机工作区与 Reader；第 7 节验收产品内置 AI Agent；第 8 节检查停止后的端口状态。

本手册中的“操作 Agent”是负责安装的助手；“产品内置 AI Agent”是本机 Codex CLI。两者是不同程序。发布包不包含 Codex 登录凭据，也不替用户登录。

## 1. 硬性规则

1. **只读微信**。不得实现、开启、尝试或“临时改代码”以支持自动发送微信消息。
2. **授权范围只能由使用者本人决定**。首次只选一个联系人作为起点，之后使用者可以在“设置 → 联系人与会话 → 后台监听”自行增减，最多 20 个一对一会话。Agent 不得代选或默认全选；群聊与公众号不在可选范围。
3. 部署与验收期间不得把聊天正文写入 Agent 输出、终端日志、截图、报告或剪贴板。Reader 将消息交给同一台电脑的 API 回环地址并保存在本机。只有使用者明确同意后，才可通过产品内置 AI 发起真实会话请求；该请求会把当前获准会话的必要上下文交给用户登录的 Codex CLI/OpenAI 服务处理。不得用真实正文排查安装问题。
4. 不得修改、删除、移动当前用户的 %LOCALAPPDATA%/WeChatAiCopilot 下既有数据。若怀疑损坏，先停止并向使用者报告。
5. 不得关闭杀毒软件或添加安全排除项。Agent 不得自行打开管理员 PowerShell、输入管理员凭据或绕过 UAC。PostgreSQL 的 winget 安装为机器范围，Windows 可能显示 UAC；向使用者说明后等待本人批准。若使用者没有管理员权限或不批准，停止并报告。
6. 失败时报告**错误码原文、执行命令、期望结果、实际结果**，不要静默重试超过两次。

## 2. 核对交付文件与电脑条件

维护者应把下面四个文件放在同一目录，并把这四个文件都提供给操作 Agent：

| 文件 | 检查 |
|---|---|
| WeChatAiCopilot-<版本>-win-x64.zip | 只使用维护者提供的版本 |
| 同名 .zip.sha256 | 安装脚本会强制核对；不得跳过 |
| Install-FromPackage.ps1 | 安装入口 |
| Agent_Install_Runbook.md | 本手册；发布时应与安装文件一起提供 |

若文件缺失、出现多个版本、文件名不匹配或来源不可信，暂停并向维护者索取完整一致的一组文件。不要从旧聊天、缓存或任意 Git 分支拼装安装材料。

以当前 Windows 普通用户运行以下检查；不要输出机器上其他应用的信息：

~~~powershell
[Environment]::Is64BitOperatingSystem
$PSVersionTable.PSVersion
Get-Command powershell.exe, winget.exe -ErrorAction SilentlyContinue | Select-Object Name, Source
Get-Process -Name Weixin, WeChat, 微信 -ErrorAction SilentlyContinue |
    Select-Object -First 3 ProcessName, Id
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs/WeChatAiCopilot'
[pscustomobject]@{ ExistingProductData = Test-Path -LiteralPath $stateRoot; ExistingProgramDirectory = Test-Path -LiteralPath $installRoot }
Get-NetTCPConnection -State Listen -LocalPort 5080, 55432 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
~~~

继续条件：

- 64 位 Windows 10/11 和 Windows PowerShell 5.1。用当前用户运行，不要依赖 PowerShell 7 的特有语法。
- PostgreSQL 13 或更高大版本会做实际兼容性探测，推荐 18。Setup 会复用兼容的已安装版本；新建数据目录时优先选 18，未找到可用版本才通过 winget 安装 18。最低 SQL 兼容基线为 13（PostgreSQL 13 起提供内置 `gen_random_uuid()`）；但 PostgreSQL 13 已结束上游维护，Setup 会警告，建议迁移到仍受维护的版本（[上游版本政策](https://www.postgresql.org/support/versioning/)）。
- PostgreSQL 数据目录只能由相同大版本的数据库程序打开。已有本机数据时，Setup 会按 `PG_VERSION` 选择精确匹配的大版本；若缺少匹配程序，会安装该版本或明确报错，不会用别的大版本试开或自动升级数据目录。
- Reader 支持 64 位 Python 3.10 或更高版本，推荐 3.11。Setup 会先用实际导入探测随包原生依赖；若 ABI 不兼容，会询问是否从 PyPI 为检测到的 Python 重新准备依赖。没有可用 Python 时会通过 winget 安装推荐的 3.11。
- Reader 上游模块 `1.2.7.0` 是产品为发送者身份解析修复所锁定并校验的内部依赖版本；它不会限制用户电脑安装的 Python、Codex CLI 或微信客户端版本。
- 发布包包含自包含 .NET 应用和 Reader runtime，接收电脑不需要 .NET SDK。通常也不需要 PyPI；只有目标机 Python 与随包本机扩展 ABI 不匹配时，才需要 PyPI 重新准备。
- 已安装并由使用者登录的官方 Windows 微信桌面版，停留在聊天窗口。产品不按微信版本号硬拦截；Reader 会尝试发现和打开当前账号的本机数据，实际格式不兼容时报告错误。Weixin、WeChat 或微信进程名均会被识别；进程存在不代表已登录，必须由使用者确认。
- 需要产品内置 AI 时，使用官方 Codex CLI。程序不设固定 CLI 版本号；安装后检查命令、登录状态和一次实际请求，遇到不兼容错误再提示升级。
- 端口 5080 或 55432 若已被占用，先查明是否属于本产品；不得结束不明进程。
- 如果 %LOCALAPPDATA%/WeChatAiCopilot 已经存在，这不是全新环境。先询问使用者是升级还是沿用；不删除、覆盖或搬动其中任何文件。
- 若 Programs/WeChatAiCopilot 已存在，先确认是否为本产品旧版；安装会镜像替换该程序目录，移除目录内不属于新包的文件。

电脑缺少 winget 时，请使用者通过 Windows 的 App Installer 安装或修复后重试。默认 PostgreSQL 18 的 winget 安装清单标注为机器范围；全新环境可能要求使用者本人批准 UAC（[PostgreSQL 18 官方清单](https://github.com/microsoft/winget-pkgs/tree/master/manifests/p/PostgreSQL/PostgreSQL/18)）。公司管控、软件源不可访问或杀软拦截时，暂停并报告。其他安装步骤意外要求提权时停止并报告。

## 3. 安装发布包

执行前告知使用者：安装脚本会核对 zip 与清单；缺少兼容数据库时会通过 winget 安装推荐版 PostgreSQL 18，缺少 Python 时会安装推荐版 Python 3.11，并更新程序目录。已安装的 PostgreSQL 13+ 与 Python 3.10+ 会先按实际版本和运行时依赖检查；若已有私有数据库，必须使用其 `PG_VERSION` 对应的大版本。PostgreSQL 13 会显示维护状态警告。PostgreSQL 是机器范围安装，Windows 可能弹出 UAC，需使用者本人批准；Agent 不处理密码。聊天数据库位于另一个用户数据目录。

在包含四个交付文件的目录运行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./Install-FromPackage.ps1 -PackageZip "./WeChatAiCopilot-<版本>-win-x64.zip" -RunSetup
~~~

不要使用 -SkipHashCheck。等待安装完成，确认输出中出现 SHA-256 一致、清单核对通过、Reader 依赖导入自检通过，以及最后的“准备完成。”。安装输出可能出现 [RUN]、[SKIP] 或 [WARN]；不是每一行都应以 [OK] 开头。

安装会把程序放入当前用户的 %LOCALAPPDATA%/Programs/WeChatAiCopilot。首次安装不需要 .NET SDK；Setup 会检查 PostgreSQL 13+ 和 64 位 Python 3.10+，并为已有数据库匹配其精确 PostgreSQL 大版本。Python 3.11 与 PostgreSQL 18 是推荐值，不是唯一可接受版本；Python 通过依赖导入自检，PostgreSQL 通过工具版本、数据目录版本及数据库兼容探针检查。PostgreSQL 13 会提示上游已停止维护。若提示包内 Reader 文件缺失或哈希不符，停止并请维护者重新生成发布包。

## 4. 准备产品内置 AI Agent（仅在使用者需要 AI 建议时）

正式启动器会把产品 Agent 运行时设为 Codex CLI。缺少 CLI 或未登录时，工作区和 Reader 可以启动，但生成建议/咨询不能算就绪。

1. 检查当前 Windows 用户是否已有 Codex CLI：

~~~powershell
Get-Command codex.exe, codex.cmd, codex -ErrorAction SilentlyContinue |
    Select-Object -First 1 Name, Source
~~~

2. 若没有安装，向使用者说明需要 OpenAI 官方 Codex CLI，并在使用者同意后按[官方 Windows 安装说明](https://github.com/openai/codex#quickstart)安装。官方 Windows 安装命令：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "irm https://chatgpt.com/codex/install.ps1 | iex"
~~~

不安装第三方改包，不猜测 npm 平台包版本，不索取或粘贴 API key。安装器若要求管理员权限、被安全软件拦截或网络不可用，暂停并报告。

3. 安装后打开新的普通 PowerShell 窗口，检查 CLI 版本（仅用于诊断）和登录状态：

~~~powershell
codex --version
codex login status
~~~

未登录时运行 codex login，由使用者在浏览器中完成账号登录。程序不硬性要求某个 Codex CLI 版本；之后还需在第 7 节验证实际请求。若当前 CLI 命令、登录或协议不兼容，先记录错误码，再按官方渠道更新。Agent 不读取或记录认证文件、浏览器回调、token 或账户凭据。若命令不可用或登录状态不明确，不要把产品 Agent 标记为就绪。

4. 若使用者不愿把获准会话的必要上下文交给其 Codex/OpenAI 服务，不发起真实建议请求；可以继续完成本机工作区与 Reader 验收，并明确注明“产品内置 AI Agent 未验证”。需要代理时只采用使用者提供的代理地址；正式启动器的 network.json 代理只影响 AI CLI，不会修复 winget/Python 安装网络。

## 5. 首次启动与授权

先由使用者确认微信已登录。再解释首次启动默认会把所选会话最近最多 5,000 条可读文本消息补录到本机私有数据库，并询问是否允许这项本机历史补录。

- 同意本机补录：双击桌面的“知间 启动”。
- 不同意本机补录：在安装目录执行以下命令，Reader 仍会监听新消息：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Start-FormalProject.ps1 -SkipInitialHistory
~~~

首次运行可能显示本机联系人清单。让使用者亲自输入一个一对一联系人的编号；Agent 不得代选或修改授权配置。成功后浏览器会自动打开本机工作区并建立一次性会话；不要手工复制令牌或把本地 URL 转发给他人。

若用户选错联系人，应从正式启动入口重新授权，并由使用者再次选择。不得直接编辑 scope.json 或数据库。

## 6. 验收本机工作区与 Reader

在普通 PowerShell 检查 HTTP 状态码；不要显示或保存响应正文：

~~~powershell
$ready = Invoke-WebRequest -Uri 'http://127.0.0.1:5080/health/ready' -UseBasicParsing -TimeoutSec 10
$home = Invoke-WebRequest -Uri 'http://127.0.0.1:5080/' -UseBasicParsing -TimeoutSec 10
[pscustomobject]@{ ReadyStatus = $ready.StatusCode; HomeStatus = $home.StatusCode }
~~~

两者都应为 200。浏览器中确认左侧显示使用者选择的会话，Reader 状态为“监听中”。如需核对方向，只看方向标签或状态图标，不打开、引用、截图或抄写正文；无法解析的发送者应显示未知。

若历史补录被跳过或失败，工作区与实时 Reader 仍可能正常。只记录历史任务状态、错误码和数量，不把部分导入说成完整。

## 7. 验收产品内置 AI Agent

只有使用者已完成 Codex CLI 登录并明确同意用获准会话上下文进行一次 AI 请求后，才在工作区点一次“生成建议”。仅确认请求成功并返回三条非空建议；不要复制、朗读、截图或写入报告建议内容。只记录成功/失败、耗时和错误码。未取得这项同意时，保持 AI 验收为“未执行”，不得假装通过。

## 8. 停止与端口回收

双击“知间 停止”，随后确认本机端口不再监听：

~~~powershell
Get-NetTCPConnection -State Listen -LocalPort 5080, 55432 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
~~~

预期无输出。若仍有监听，记录 PID 和程序路径并报告；不要强制结束陌生进程。

## 9. 故障处理与交付回报

失败时报告准确命令、期望结果、实际结果、稳定错误码、Windows/PowerShell/Python/PostgreSQL/Codex CLI 版本。最多重试两次。不要输出环境变量、访问令牌、账号/联系人 wxid、数据库路径、消息正文、提示词或 Codex 认证文件。

| 现象/错误码 | 处理 |
|---|---|
| zip SHA-256 不一致、清单不一致 | 立即停止；请维护者从可信来源重新提供四个文件。 |
| winget 不存在或 PostgreSQL/Python 安装后仍未检测到 | 检查 App Installer、Windows 软件源与网络；不要提权或静默重复安装。 |
| PostgreSQL 大版本与现有数据目录不匹配 | 停止启动；不要手动改 `PG_VERSION` 或用其他大版本打开。安装精确匹配版本，或由维护者安排备份、`pg_upgrade`/恢复迁移和演练。 |
| WECHAT_DATABASE_UNAVAILABLE | 让使用者确认桌面微信已登录且停在聊天窗口；普通权限重试一次。若微信由管理员启动，不要自动提权，先告知使用者。若确认登录与权限无误，记录微信客户端版本，再尝试更新到官方当前稳定版；Reader 不绕过不兼容的数据库格式。 |
| SELF_WXID_UNAVAILABLE、ACTIVE_ACCOUNT_MISMATCH | 停止，不改 scope 或数据库；确认微信当前账号与用户授权范围后反馈错误码。 |
| READER_UPSTREAM_SENDER_RESOLUTION_UNAVAILABLE | 固定版 Reader runtime 未正确安装；从已核验的发布包重跑 Setup。不要使用短 ID 猜方向。 |
| CODEX_CLI_START_FAILED | 确认 Codex CLI 在当前用户 PATH 中可运行并已登录；程序不要求固定 CLI 版本。若命令启动但协议不兼容，记录 `codex --version` 与错误码，再从官方渠道更新；必要时检查 CODEX_CLI_AUTH_REQUIRED、CODEX_CLI_CONFIG_INVALID 或 CODEX_CLI_EXIT_NONZERO。 |
| CODEX_CLI_NETWORK_FAILED、RUNTIME_TIMEOUT | 先检查使用者提供的网络/代理和 Codex 登录状态；不得猜代理端口或转发聊天内容排障。 |
| 端口占用 | 先确认进程归属；只有确认属于本项目时使用“知间 停止”，不强杀其他程序。 |

最终分别回报**本机工作区与 Reader**（通过/未通过）和**产品内置 AI Agent**（通过/用户未同意/未配置/未通过）。用户未同意模型请求时，明确写明 AI 验收未执行，不要把它算作通过。
