# 本机开发与运行手册

本文只描述正式仓库的运行方法。技术验证工程不属于本仓库，也不是本产品的运行依赖。

## 1. 当前部署边界

- 首版运行形态是同一台 Windows 电脑上的 ASP.NET Core API、PostgreSQL、Web 页面和本机 Reader。
- Reader 只允许配置一个微信账号和一个 Direct 会话；不会自动发送或回复微信消息。
- 本轮开发验收使用合成消息和临时数据库。真实微信账号、真实联系人和真实消息尚未用于验收。
- `Agent:Runtime` 默认关闭。只有本机 Codex CLI 已配置、用户明确启用后，AI 请求才会把当前任务所需的会话上下文交给该 CLI。
- 未验证远程部署、多人并发、高可用、跨 shard 消息唯一性或所有微信版本兼容性。

## 2. 环境

- Windows 10/11
- .NET 10 SDK
- PostgreSQL 服务；本仓库的迁移入口由 API 内置，不需要单独运行 SQL 文件
- Python 3.11（仅运行 M1 Reader）
- PowerShell

在仓库根目录执行：

```powershell
dotnet --version
dotnet build .\WeChatAiCopilot.sln
dotnet test .\WeChatAiCopilot.sln
python -m unittest discover -s .\tests\reader_tests -p 'test_*.py' -v
```

## 3. 初始化数据库与启动 API

1. 在 PostgreSQL 创建一个专用于本机运行的空数据库和运行身份。运行身份只需访问此数据库并执行仓库迁移所需的 DDL；不要使用 PostgreSQL superuser 运行 API。数据库不应与技术验证项目共用。
2. 在启动 API 的 PowerShell 进程中设置连接字符串和随机 Bearer token。不要把值写入仓库、启动参数、截图或日志：

```powershell
$env:ConnectionStrings__FormalDatabase = '<本机 PostgreSQL 连接字符串>'
$env:Authentication__WebBearerToken = '<随机生成的 Web token>'
$env:Authentication__ReaderBearerToken = '<另一个随机生成的 Reader token>'
$env:Web__AllowedAccountExternalId = '<获准账号外部 ID>'
$env:Web__AllowedConversationExternalId = '<获准 Direct 会话外部 ID>'
$env:Reader__AllowedAccountExternalId = '<同一获准账号外部 ID>'
$env:Reader__AllowedConversationExternalId = '<同一获准 Direct 会话外部 ID>'
dotnet run --project .\src\WeChatAiCopilot.Api
```

API 启动时按序应用仓库内尚未登记的数据库迁移。迁移按版本、SHA-256 校验和记录；每个迁移在事务内应用，并以 PostgreSQL advisory lock 避免并发执行。已执行迁移被修改时，API 会拒绝继续启动；应新增迁移，不要改写已发布编号。

Web 和 Reader 使用不同 token。Web token 只输入本机浏览器当前页面，Reader token 放入 LocalAppData 下的 Reader 设置文件。API 不把它们写入业务表。若缺少身份或范围配置，对应 API 会失败关闭。

检查 API：

- `GET http://127.0.0.1:5080/health/live`：进程存活
- `GET http://127.0.0.1:5080/health/ready`：数据库连接就绪；未配置数据库时返回 503
- 浏览器打开 `http://127.0.0.1:5080/`

开发时如需固定回环地址，可在启动 API 前设置 `$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'`。不要将未经保护的监听地址暴露到局域网或公网。

## 4. 启用 Codex CLI

默认不启用 Agent。要进行本机受限运行时验收：

1. 确认本机 Codex CLI 已安装，并由 CLI 自己完成登录。
2. 设置 `Agent__Runtime=CodexCli`，配置所需 persona、skill 版本和运行预算，重启 API。
3. 从 Web 发起建议或咨询；只有这类明确操作会执行模型请求。

进程使用只读沙箱、临时工作目录、结构化输出 schema 和有界输入输出；不开放 Shell、MCP、远程插件、Web 搜索或微信发送工具。模型请求包含当前获准会话的必要上下文，因此应依照本机数据和所用 Codex 账户的隐私要求使用。不要在日志或问题报告中粘贴聊天正文、提示词、token 或 `CODEX_HOME` 内容。

本轮不含聊天数据的 Codex CLI smoke 用时 117 秒；合成建议 API 在 180 秒运行预算下完成。60 秒预算在当前本机延迟下会超时。实际部署时按目标机器的模型响应时间设置 Agent:Budget:MaxDurationSeconds；这项配置同时是运行时硬截止时间。

本机测得一次不含聊天数据的结构化 CLI smoke 用时约 117 秒；合成建议 API 在 180 秒运行预算下完成。当前环境使用 60 秒预算会超时。设置 Agent:Budget:MaxDurationSeconds 时应按目标机器的实际响应时间留出余量。

## 5. 准备与运行 Reader

完整安装、单会话配置、实时读取和有界历史补录命令见 [M1 Reader 手册](../../src/WeChatAiCopilot.Reader/README.md)。Reader 第一次连接前，操作者必须确认目标账号与会话；不要用未授权数据做试跑。

Reader API 传输默认关闭。只有 Reader 本机设置、服务端 Reader token 和服务端账号/会话 allowlist 三者都配置后，才开启提交。真实微信链路尚未在本轮回归中验收。

## 6. 备份与恢复

聊天正文、人工记忆、画像、Agent 运行结果和校准状态都可能保存在 PostgreSQL 中；备份文件按敏感个人数据保护，仅存放在当前 Windows 用户可访问的加密/受限目录。

备份前暂停 API 与 Reader 写入，然后在 PowerShell 中执行（按本机环境替换数据库名、用户名和路径；`pg_dump` 会按 PostgreSQL 客户端配置请求认证）：

```powershell
pg_dump --format=custom --no-owner --file 'D:\PrivateBackups\copilot-2026-10-10.dump' --host 127.0.0.1 --port 5432 --username copilot_app --dbname wechat_ai_copilot
pg_restore --list 'D:\PrivateBackups\copilot-2026-10-10.dump'
```

恢复演练应先创建一个新的空数据库，再恢复备份；不要先对唯一的现有数据库运行 `pg_restore --clean`：

```powershell
createdb --host 127.0.0.1 --port 5432 --username <本机数据库管理员> wechat_ai_copilot_restore_check
pg_restore --no-owner --no-privileges --host 127.0.0.1 --port 5432 --username <恢复角色> --dbname wechat_ai_copilot_restore_check 'D:\PrivateBackups\copilot-2026-10-10.dump'
```

恢复后将 API 临时连接到恢复库，确认 `/health/ready` 成功、迁移校验无错误，并检查获准会话、记忆与校准状态，再切换服务。首次上线或升级数据库前先做可读回的备份。启动器在检测到新迁移时会自动执行 `scripts\Backup-FormData.ps1 -Verify -Keep 10`：完整备份后用 `formal_restore_drill` 库还原并比对行数，失败则中止启动。仍没有定期计划备份、异地副本或迁移自动回滚机制；长期仍需操作者定期手动备份和恢复演练。

## 7. 迁移失败处理

- 不删除 `copilot.schema_migrations` 记录来绕过校验。
- 不修改已经登记的迁移 SQL；创建下一个编号的前向迁移并保留旧文件。
- 若数据库迁移中断，先查看 API 错误和 PostgreSQL 状态，保留数据库副本后再处理。
- 当前恢复方式是从升级前备份恢复到新数据库，再将 API 指向恢复库；尚无自动反向迁移脚本。

## 8. 已知限制

- 单账号、单 Direct 会话配置；Reader 不监听群聊，不提供自动发送。
- 历史补录最多读取 5,000 条文本消息，读取遇到分片错误或触及上限时整体失败，不把部分结果报作完成。
- `wx4-shard-v2` 来源键包含分片指纹与消息表；单测验证相同 local ID 跨分片不撞号。PostgreSQL 来源合并的集成验收仍需隔离测试库。
- 账号 wxid 和消息来源的 `SourceConfirmed` 方向决定画像/自动记忆是否可加载；未知发送者保持 Unknown，人工记忆独立保留。
- 本轮集成和页面验收使用合成数据；实际微信版本兼容、真实账号接入、隐私告知与模型账户策略需在目标本机单独确认。
- 不支持远程部署、多用户权限管理、复杂多会话监听、附件、自动化发送或生产级高可用。
