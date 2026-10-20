# 本机开发与运行手册

本文只描述正式仓库的运行方法。技术验证工程不属于本仓库，也不是本产品的运行依赖。

## 1. 当前部署边界

- 首版运行形态是同一台 Windows 电脑上的 ASP.NET Core API、PostgreSQL、Web 页面和本机 Reader。
- Reader 连接一个当前活动微信账号，可监听同一账号下最多 20 个明确授权的 Direct 会话；不会自动发送微信消息。
- 当前真实账号仅完成两条身份元数据核验（Outgoing/Self 与 Incoming/Peer）；未读取正文列。连续监听、停止后补读、切号/重启及真实多会话隔离仍待验收。
- 正式一键启动器会设置 Agent:Runtime=CodexCli；直接运行 API 时 Agent runtime 默认失败关闭。Codex CLI 未安装或未登录时，工作区可启动但 Agent 建议不可用。
- 未验证远程部署、多人并发、高可用、跨 shard 消息唯一性或所有微信版本兼容性。

## 2. 环境

- Windows 10/11
- .NET 10 SDK
- PostgreSQL 13+；推荐 18。本仓库的迁移入口由 API 内置，不需要单独运行 SQL 文件；已有数据目录只能使用相同大版本的程序；PostgreSQL 13 会提示其已停止上游维护
- 64 位 Python 3.10+（仅运行 M1 Reader；推荐 3.11，实际可用性由原生依赖导入检查确认）
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

## 4. 产品内置 AI Agent

正式启动器会把产品 Agent 运行时设置为 Codex CLI；缺少 CLI 或登录未完成时，API/工作区仍可启动，但建议和咨询不可用。直接以 dotnet run 启动 API 时，Agent runtime 默认失败关闭，需显式配置。

目标机没有 CLI 时，按[OpenAI 官方 Codex CLI Windows 安装说明](https://github.com/openai/codex#quickstart)安装 standalone CLI，由使用者运行 codex login 并自行登录。程序不硬性检查某个 CLI 版本；在新 PowerShell 查看 codex --version（诊断信息）和 codex login status，并通过一次实际请求确认兼容。不要收集 API key、token、认证文件或浏览器回调。

用户明确发起建议或咨询时，当前获准会话的必要上下文会交给该用户登录的 Codex CLI/OpenAI 服务处理。部署验收不能擅自触发真实会话请求；需先取得明确同意。未同意时只验收本机 API/Reader，并将 AI 标为“未执行”。

运行时使用只读沙箱、临时工作目录、结构化输出 schema 和有界输入输出；不开放 Shell、MCP、远程插件、Web 搜索或微信发送工具。日志与问题报告不得包含聊天正文、提示词、token 或 CODEX_HOME 内容。代理配置只影响 AI CLI 出口，不替代 winget/Python 下载网络配置。

无聊天状态的 Codex CLI smoke 曾用时 117 秒；合成建议 API 在 180 秒运行预算下完成。目标机器需按实际网络与 CLI 延迟设置运行预算；不要把本机实测时间当作每台新电脑的保证。

## 5. 准备与运行 Reader

正式启动器自动生成受限 LocalAppData 设置、Reader token 和服务端 allowlist，并启用 API loopback 传输。Reader 支持一个微信账号下最多 20 个明确授权的 Direct 会话。直接运行 Reader 或开发 API 时，需自行配置本机 Reader settings、服务端 Reader token 与账号/会话 allowlist；不得复制别人的令牌。

Reader 仅从用户登录的本机微信数据读取。首次选定会话后，启动器默认补录最多 5,000 条可读文本消息到本机 PostgreSQL；如使用者不允许本机补录，可通过启动参数跳过。AI 生成请求会额外把必要上下文交给用户登录的 Codex CLI/OpenAI 服务，必须由用户明确发起并同意。

本机真实账号的两条身份元数据记录已核验：一条 Outgoing/Self、一条 Incoming/Peer，未读取正文列或提交到外部 AI。连续监听、停止后补读、切号/重启和真实多会话隔离仍待验收。

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
- 首次启动补录最多 5,000 条可读文本消息；设置页可提交最近 90 天或本机全部历史任务，全量单任务上限为 200,000 条，具体范围与截止时间按界面提示。
- `wx4-shard-v2` 来源键包含分片指纹与消息表；单测验证相同 local ID 跨分片不撞号。PostgreSQL 来源合并的集成验收仍需隔离测试库。
- 账号 wxid 和消息来源的 `SourceConfirmed` 方向决定画像/自动记忆是否可加载；未知发送者保持 Unknown，人工记忆独立保留。
- 本轮集成和页面验收使用合成数据；实际微信版本兼容、真实账号接入、隐私告知与模型账户策略需在目标本机单独确认。
- 不支持远程部署、多用户权限管理、复杂多会话监听、附件、自动化发送或生产级高可用。
