# Agent 安装执行手册（在朋友电脑上由 AI Agent 阅读并执行）

面向对象：**在朋友电脑上运行的编码 Agent**（Qoder / Codex / Claude 等）。
目标：把「知间 · 微信 AI 沟通助手」装到能正常使用，全程不需要使用者手动输入命令，只在必须本人决定的地方询问一次。

## 0. 硬性规则（任何情况下不得违反）

1. **只读微信**。不得实现、开启、尝试或“临时改代码”以支持自动发送微信消息。
2. **授权范围只能由使用者本人决定**。首次启动只选一个联系人作为起点，之后使用者可以在
   「设置 → 联系人与会话 → 后台监听」里自行增减，上限 20 个一对一会话。Agent 不得替使用者
   挑选或默认全选，但当使用者想多用几个联系人时，**应当告诉他怎么加，而不是阻止他**。
   群聊与公众号不在可选范围内，这是产品设计，不要绕过。
3. 不得把聊天正文写入日志、终端输出、报告、剪贴板或任何网络请求。需要举证时只给条数、日期、错误码。
4. 不得修改、删除、移动 `%LOCALAPPDATA%\WeChatAiCopilot` 下的既有数据。若怀疑损坏，先停止并向使用者报告。
5. 不得关闭杀毒软件、不得添加系统级排除项以外的白名单；需要排除时**说明理由并请使用者确认**。
6. 不得以管理员身份运行任何步骤，除非普通权限已确认失败且使用者同意。
7. 任何一步失败：报告**错误码原文 + 你执行的命令 + 期望结果 + 实际结果**，不要静默重试超过两次。

## 1. 先确认这台电脑是否具备条件

依次执行并记录结果（PowerShell）：

```powershell
[Environment]::Is64BitOperatingSystem
Get-Command dotnet.exe,winget.exe,py.exe -ErrorAction SilentlyContinue | Select-Object Name,Source
Test-Path 'C:\Program Files\PostgreSQL\18\bin\initdb.exe','G:\PostgreSQL\18\bin\initdb.exe'
Get-Process Weixin -ErrorAction SilentlyContinue | Select-Object -First 1 Id,Path
```

判定：

| 检查 | 不满足时 |
| --- | --- |
| 64 位 Windows | 停止，告知不支持 |
| 微信桌面版正在运行且已登录 | 请使用者打开微信并登录到聊天窗口，**这一步不能代办** |
| PostgreSQL 18 | 由安装脚本用 winget 自动安装 |
| Python 3.11 64 位 | 由安装脚本用 winget 自动安装 |
| .NET SDK | **不需要**，前提是项目里已带 `artifacts\app\win-x64`。若缺失，见 §2 说明 |

若 `artifacts\app\win-x64\WeChatAiCopilot.Api.exe` 不存在且没有 .NET SDK：**不要尝试安装 SDK 或改代码**，直接告知使用者“安装包不完整，需要开发机重新生成 artifacts 目录”。

## 2. 取到程序并准备环境

**优先走发布包**（使用者从聊天工具/网盘拿到三个文件：`…-win-x64.zip`、`….zip.sha256`、`Install-FromPackage.ps1`）。
先向使用者确认这三个文件放在哪个目录，然后在该目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Install-FromPackage.ps1 -PackageZip "<安装包文件名>.zip" -RunSetup
```

安装脚本会核对 SHA-256、逐文件核对 `release-manifest.json`、镜像安装到
`%LOCALAPPDATA%\Programs\WeChatAiCopilot`，再自动接着做下面的环境准备。
**哈希核对不通过就停止**，把期望值与实际值一起回报，不要加 `-SkipHashCheck` 蒙过去。
Windows 弹出未签名提示时，说明原因并让使用者本人决定，不要为了绕开提示关闭系统安全功能。

装完后，脚本所在目录就是后续步骤里的 `<项目目录>`（结构与仓库 `product` 一致）。

只有当使用者是开发者、要求从源码运行时，才走这条：

```powershell
git clone https://github.com/lan257/WeChat_AI_Copilot.git
git -C WeChat_AI_Copilot checkout feat/v8-product-implementation
cd WeChat_AI_Copilot\product
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Setup-TrialMachine.ps1 -Yes
```

两种方式最后都期望：每一行以 `[OK]` 开头，最后出现 `准备完成。`。

- 出现 `[RUN]` 表示正在安装/下载，属正常，等待即可。
- 出现 `[FAIL]` 按提示处理；只有“既没有预构建应用也没有 .NET SDK”这一条需要回到 §1。
- 脚本可重复执行，不会删除已有数据。

## 3. 首次启动与授权（需要使用者本人做一次选择）

```powershell
# 双击桌面「知间 启动」，或：
.\启动正式项目.cmd
```

首次运行会列出本机可读取的**一对一会话编号清单**，请使用者自己输入编号。

- **必须让使用者本人选**。Agent 不得代选，也不得事后修改 `scope.json` 来换联系人。
- 这里选的是**起点**，不是唯一可用的人。装好后告诉使用者：想让别人也进来，去
  「设置 → 联系人与会话 → 后台监听」打开开关即可（最多 20 个一对一会话），不需要重装、也不需要改配置。
- 群聊与公众号不会出现在清单里，这是设计限制，不是 bug。
- 选错时可以重跑：`.\启动正式项目.cmd -ReauthorizeContact`（同样需要使用者本人重新选）。

成功标志：终端出现 `正式工作区已启动…`，并且浏览器自动打开工作区、左侧能看到所选联系人。

## 4. 安装后自检（Agent 必须逐条执行并记录）

```powershell
curl.exe -s http://127.0.0.1:5080/health/ready          # 期望 {"status":"ready"...} 且 HTTP 200
curl.exe -s -o NUL -w "%{http_code}\n" http://127.0.0.1:5080/   # 期望 200
```

界面侧再确认（浏览器里操作，不要截图聊天内容）：

1. 左侧出现所选联系人，聊天窗口有真实历史消息；
2. 顶部状态为“监听中”（若为“采集异常”见 §5）；
3. 读取器自动识别本人和联系人方向；检查聊天消息中能解析的发送者已标为本人或联系人，不可解析的记录显示「发送者未知」；
4. 右侧 AI 面板能生成三条建议（首次可能 30–60 秒）；
5. 双击桌面「知间 停止」后，`5080` 与 `55432` 端口都不再监听、无残留进程。

## 5. 故障决策树（错误码来自真实观测）

```
现象：启动报 WECHAT_DATABASE_UNAVAILABLE
  → 微信未登录 / 停在登录界面 / 正在重启（密钥只在内存里）
  → 让使用者把微信打开到聊天窗口，等 10 秒，重跑「知间 启动」
  → 仍失败：检查是否 32 位 Python（必须 64 位）；检查微信是否以管理员运行（若是，脚本也要管理员运行）
  → 不要试图重装微信或改数据库

现象：网页显示“本机会话尚未建立，请从一键启动入口打开网页”
  → 一次性授权只服务于启动脚本自动打开的那个标签页
  → 关闭该标签页，重新双击「知间 启动」；不要手工输入 URL，也不要把链接转发给别人

现象：AI 任务长时间停在“正在执行模型”，最终 RUNTIME_TIMEOUT
  → 模型出口不可达。先在终端确认 Codex CLI 能否正常出结果
  → 需要本机代理时：%LOCALAPPDATA%\WeChatAiCopilot\Formal\network.json
     {"proxyUrl":"http://127.0.0.1:<端口>","noProxy":"127.0.0.1,localhost"}  然后重启
  → 代理地址必须来自使用者本人提供的信息，不要猜测端口

现象：FORMAL_API_REJECTED / READER_SCOPE_NOT_ALLOWED
  → 采集范围与已授权目标不一致。不要手动改数据库；收集 api.log 末尾 20 行（不含正文）反馈给开发者

现象：历史补录条数远小于预期
  → 当前补录窗口是最近 90 天，更早的不在范围内；5000 是单任务行上限不是目标量
  → 这是已知限制，需求见 docs/product/proposals/2026-10-14_full-local-history-import.md，不要绕过

现象：杀毒软件删除只读组件 / 报毒
  → 读微信进程内存取密钥会被启发式判定。向使用者说明原理，由使用者决定是否加入排除项，
     然后重跑 scripts\Setup-TrialMachine.ps1

现象：端口被占用
  → 先双击「知间 停止」再启动；仍占用则报告占用进程名与 PID，不要强杀陌生进程
```

## 6. 收尾：向开发者反馈的格式

请使用者（或 Agent 汇总）提供，**不要包含任何聊天内容**：

1. 第 4 节自检 5 项各自的通过/失败，失败项写屏幕原文；
2. 首次生成建议等待秒数；等待期间是否知道发生了什么；
3. 三条建议里会真的发出去几条；不像人话的是第几条、哪里不像；
4. 找不到的功能（换人设 / 换分析深度 / 补录进度 / 停用监听）；
5. 最希望有而当前没有的一件事；觉得多余的一件事；
6. 隐私顾虑原话；
7. 环境信息：Windows 版本、微信版本、Python 位数、是否使用代理（只说有无，不说地址）。

## 7. 卸载

```powershell
# 双击桌面「知间 停止」，然后：
Remove-Item "$env:LOCALAPPDATA\WeChatAiCopilot" -Recurse -Force   # 删除全部本机数据（含聊天记录），需使用者确认
Remove-Item "$([Environment]::GetFolderPath('Desktop'))\知间 启动.lnk","$([Environment]::GetFolderPath('Desktop'))\知间 停止.lnk" -ErrorAction SilentlyContinue
```

删除数据目录前必须向使用者二次确认，因为里面是真实聊天记录且不可恢复。
