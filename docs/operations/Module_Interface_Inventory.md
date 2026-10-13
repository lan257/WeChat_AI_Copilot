# 模块接口清单

本清单记录正式仓库当前的可调用边界，供联调与验收查阅。权威字段定义仍以 [共享契约](../design/WeChat_AI_Copilot_Module_Design_v1/00_Shared_Contracts.md) 和各模块代码为准。

## 模块之间的主要数据流

| 调用方 → 所属方 | 接口 | 作用域 / 约束 |
|---|---|---|
| M1 Reader → M2 消息域 | `RawMessageEventV1`、`HistoryBatchV1`；`GET /api/reader/targets`、`POST /api/reader/status`、消息与历史写入接口 | Reader Bearer 身份；一个活动账号下最多 20 个显式 Direct 目标；目标版本及会话归属由服务端验证。 |
| M1 Reader → M2 目录 | `/api/reader/discovery-jobs/next` 与目录提交接口 | 只有 Web 用户显式创建任务后读取会话目录；只提交 Direct 会话外部 ID 和显示名，不提交消息摘要或正文。 |
| M5 Web → M2 消息域 | `GET /api/accounts`、`GET /api/accounts/{accountId}/conversations`、`GET /api/conversations/current*` | Web 身份；目录限于正式数据库已知账号；选中 UUID 会通过数据库父子关系解析，`/current` 无参数时继续使用兼容配置。 |
| M5 Web → M1 Reader | `PUT /api/accounts/{accountId}/reader-targets`、`GET /api/reader/status` | Web 身份；监听集合与当前查看会话分离，目标替换采用版本校验且上限为 20。 |
| M2 消息域 → M3 上下文 | `IMessageQueryV1`、消息页 / source identity / sender calibration | 查询参数必须经过获准会话范围；方向未确认或校准版本不符的派生内容不能进入 Agent 上下文。 |
| M5 Web → M3 上下文 | `GET/POST/PUT/DELETE /api/conversations/current/memory[/{memoryId}]` | Web Bearer 身份；记忆与证据受账号、联系人、会话和参与者身份校验。 |
| M3 上下文 → M4 Agent | `ContextBundleV1`、`IContextToolSessionV1` | 初始上下文和后续历史/证据工具绑定同一获准会话；工具调用有界并复核消息集修订。 |
| M5 Web → M4 Agent | `GET /api/ai/personas`、`GET /api/ai/execution-profiles`、建议批次、咨询 Thread/Turn、运行状态 | Web Bearer 身份；四档由服务端映射为上下文/检索预算；咨询历史仅属于创建它的账号与会话；建议恰有 3 条非空、互异结果。 |
| M6 平台 → M1–M5 | Bearer filter、PostgreSQL migration runner、结构化 Codex CLI executor、M3 outbox dispatcher | 只提供受限基础能力；业务模块不获得 Shell、任意数据库或微信发送权限。 |

## HTTP 接口

| 方法与路径 | 模块 | 认证 | 当前用途 |
|---|---|---|---|
| `GET /health/live` | M6 | 无 | API 进程存活探针。 |
| `GET /health/ready` | M6 | 无 | PostgreSQL 就绪探针；未配置或不可用返回 503。 |
| `POST /api/reader/messages` | M1/M2 | Reader token | 提交一条实时读取事件并接收幂等回执。 |
| `POST /api/reader/history-batches` | M1/M2 | Reader token | 提交有界历史批次并接收回执。 |
| `GET /api/reader/targets` | M1/M2 | Reader token | 读取当前活动账号的版本化监听目标集合。 |
| `POST /api/reader/status` | M1/M6 | Reader token | 写入 Reader 实例心跳、监听数和经过校验的错误码；不包含消息正文或凭据。 |
| `GET /api/reader/status` | M5/M6 | Web token | 查看各账号 Reader 最近心跳及运行状态；超过 20 秒标记为离线。 |
| `GET /api/system/status` | M5/M6 | Web token | 返回不含聊天正文、账号 ID 或凭据的系统状态：数据库连通性、Reader 心跳、最近 Agent 运行元数据及受限配置标志；历史补录任务标为未实现。 |
| `GET /api/accounts` | M2/M5 | Web token | 列出已登记账号及 Reader 活动状态。 |
| `GET /api/accounts/{accountId}/conversations` | M2/M5 | Web token | 列出最多 2,000 个已发现的 Direct 会话、监听状态和消息修订版本；支持显示名搜索，不返回目录摘要。 |
| `PUT /api/accounts/{accountId}/reader-targets` | M1/M2/M5 | Web token | 以 `expectedVersion` 替换活动 Reader 账号的最多 20 个监听目标。 |
| `POST /api/accounts/{accountId}/discovery-jobs` | M1/M2/M5 | Web token | 用户主动请求 Reader 只读发现本机 Direct 会话目录。 |
| `GET /api/accounts/{accountId}/discovery-jobs/{jobId}` | M1/M2/M5 | Web token | 查询会话目录发现状态和数量。 |
| `GET /api/reader/discovery-jobs/next` | M1/M2 | Reader token | 领取用户发起且仅绑定当前活动 Reader 账号的目录任务。 |
| `POST /api/reader/discovery-jobs/{jobId}/sessions` | M1/M2 | Reader token | 登记会话外部 ID 和本机显示名，不含聊天正文。 |
| `GET /api/conversations/current` | M2/M5 | Web token | 读取服务端配置会话。 |
| `GET /api/conversations/current/messages` | M2/M5 | Web token | 分页读取配置会话消息。 |
| `GET /api/conversations/current/sender-perspective` | M2/M5 | Web token | 读取发送者确认状态与视角版本。 |
| `PUT /api/conversations/current/sender-perspective` | M2/M5 | Web token | 提交明确确认后的视角变更。 |
| `GET/POST /api/conversations/current/memory` | M3/M5 | Web token | 列出或创建人工记忆。 |
| `GET/PUT/DELETE /api/conversations/current/memory/{memoryId}` | M3/M5 | Web token | 读取、更新或删除当前获准范围内的记忆。 |
| `GET /api/ai/personas` | M4/M5 | Web token | 返回服务器允许的 persona ID 与显示名。 |
| `GET /api/ai/execution-profiles` | M4/M5 | Web token | 返回 Quick、Balanced、Deep、Max 的服务端上下文与检索说明。 |
| `POST /api/ai/suggestions` | M4/M5 | Web token | 创建三条建议；请求可指定受控 `executionProfileId`，`previousBatchId` 用于关联换批。 |
| `GET /api/ai/suggestions/{batchId}` | M4/M5 | Web token | 读取属于当前获准会话的建议批次。 |
| `POST /api/ai/consultations` | M4/M5 | Web token | 创建会话咨询 Thread 并提交首轮问题。 |
| `GET /api/ai/consultations/{threadId}` | M4/M5 | Web token | 读取当前会话下属于本范围的咨询历史。 |
| `POST /api/ai/consultations/{threadId}/turns` | M4/M5 | Web token | 向绑定的 Thread 增加新一轮咨询并选择受控执行档位。 |
| `GET /api/ai/runs/{runId}` | M4/M5 | Web token | 读取不含提示词或聊天正文的运行状态。 |

## 契约与持久化归属

| 模块 | 正式责任 | 对外 / 共享边界 |
|---|---|---|
| M1 Reader | 单活动账号只读、最多 20 个服务端目标、版本化同步、Reader 心跳、用户触发的 Direct 目录发现、来源字段与有界历史补录 | D01 `RawMessageEventV1`、D02 `HistoryBatchV1`、Reader status/target/directory API；API Reader 凭据。 |
| M2 消息域 | 账号、联系人、会话、消息、来源身份、发送者校准和查询 | 消息查询接口、校准状态、`messageId` / `messageSetRevision`。 |
| M3 上下文 | 人工记忆 CRUD、证据验证、ContextBundle、画像/消息记忆沉淀与重建 | Context / Memory 契约、`contextRevision`、`IContextToolSessionV1`。 |
| M4 Agent | Runtime、persona、建议批次、咨询 Thread/Turn、运行记录 | 建议、咨询及运行 API；`IAgentRuntime` 和受限 Codex CLI executor。 |
| M5 工作区 | Web 授权、会话展示、人设、复制建议、咨询交互 | HTTP API；页面只把建议复制到剪贴板，不调用微信发送。 |
| M6 平台 | 身份过滤、迁移、运行探针、CLI 进程隔离、outbox 投递 | 共用基础设施，不接管业务数据所有权。 |

## 数据库迁移

单个 API migration runner 按 `db/migrations/NNN_*.sql` 名称顺序嵌入和执行，版本登记在 `copilot.schema_migrations`。每个迁移使用事务和校验和；已登记脚本内容不可变更。当前编号为 `001`–`014`。恢复与升级前备份步骤见 [本机开发与运行手册](Local_Operations_Guide.md)。
