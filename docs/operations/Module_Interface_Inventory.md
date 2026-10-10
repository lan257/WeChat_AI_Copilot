# 模块接口清单

本清单记录正式仓库当前的可调用边界，供联调与验收查阅。权威字段定义仍以 [共享契约](../design/WeChat_AI_Copilot_Module_Design_v1/00_Shared_Contracts.md) 和各模块代码为准。

## 模块之间的主要数据流

| 调用方 → 所属方 | 接口 | 作用域 / 约束 |
|---|---|---|
| M1 Reader → M2 消息域 | `RawMessageEventV1`、`HistoryBatchV1`；`POST /api/reader/messages`、`POST /api/reader/history-batches` | Reader Bearer 身份；服务端只接受已配置的 provider、账号、Direct 会话；来源字段保留以支持同源幂等。 |
| M5 Web → M2 消息域 | `GET /api/conversations/current`、`GET /api/conversations/current/messages` | Web 身份固定到服务器配置的单一账号/会话；调用方不能在请求中扩大范围。 |
| M2 消息域 → M3 上下文 | `IMessageQueryV1`、消息页 / source identity / sender calibration | 查询参数必须经过获准会话范围；方向未确认或校准版本不符的派生内容不能进入 Agent 上下文。 |
| M5 Web → M3 上下文 | `GET/POST/PUT/DELETE /api/conversations/current/memory[/{memoryId}]` | Web Bearer 身份；记忆与证据受账号、联系人、会话和参与者身份校验。 |
| M3 上下文 → M4 Agent | `ContextBundleV1`、`IContextToolSessionV1` | 初始上下文和后续历史/证据工具绑定同一获准会话；工具调用有界并复核消息集修订。 |
| M5 Web → M4 Agent | `GET /api/ai/personas`、建议批次、咨询 Thread/Turn、运行状态 | Web Bearer 身份；咨询历史仅属于创建它的账号与会话；建议恰有 3 条非空、互异结果。 |
| M6 平台 → M1–M5 | Bearer filter、PostgreSQL migration runner、结构化 Codex CLI executor、M3 outbox dispatcher | 只提供受限基础能力；业务模块不获得 Shell、任意数据库或微信发送权限。 |

## HTTP 接口

| 方法与路径 | 模块 | 认证 | 当前用途 |
|---|---|---|---|
| `GET /health/live` | M6 | 无 | API 进程存活探针。 |
| `GET /health/ready` | M6 | 无 | PostgreSQL 就绪探针；未配置或不可用返回 503。 |
| `POST /api/reader/messages` | M1/M2 | Reader token | 提交一条实时读取事件并接收幂等回执。 |
| `POST /api/reader/history-batches` | M1/M2 | Reader token | 提交有界历史批次并接收回执。 |
| `GET /api/conversations/current` | M2/M5 | Web token | 读取服务端配置会话。 |
| `GET /api/conversations/current/messages` | M2/M5 | Web token | 分页读取配置会话消息。 |
| `GET /api/conversations/current/sender-perspective` | M2/M5 | Web token | 读取发送者确认状态与视角版本。 |
| `PUT /api/conversations/current/sender-perspective` | M2/M5 | Web token | 提交明确确认后的视角变更。 |
| `GET/POST /api/conversations/current/memory` | M3/M5 | Web token | 列出或创建人工记忆。 |
| `GET/PUT/DELETE /api/conversations/current/memory/{memoryId}` | M3/M5 | Web token | 读取、更新或删除当前获准范围内的记忆。 |
| `GET /api/ai/personas` | M4/M5 | Web token | 返回服务器允许的 persona ID 与显示名。 |
| `POST /api/ai/suggestions` | M4/M5 | Web token | 创建三条建议；`previousBatchId` 用于换批。 |
| `GET /api/ai/suggestions/{batchId}` | M4/M5 | Web token | 读取属于当前获准会话的建议批次。 |
| `POST /api/ai/consultations` | M4/M5 | Web token | 创建会话咨询 Thread 并提交首轮问题。 |
| `GET /api/ai/consultations/{threadId}` | M4/M5 | Web token | 读取当前会话下属于本范围的咨询历史。 |
| `POST /api/ai/consultations/{threadId}/turns` | M4/M5 | Web token | 向绑定的 Thread 增加新一轮咨询。 |
| `GET /api/ai/runs/{runId}` | M4/M5 | Web token | 读取不含提示词或聊天正文的运行状态。 |

## 契约与持久化归属

| 模块 | 正式责任 | 对外 / 共享边界 |
|---|---|---|
| M1 Reader | 单会话只读取、来源字段与身份透传、有界历史补录 | D01 `RawMessageEventV1`、D02 `HistoryBatchV1` JSON；API Reader 凭据。 |
| M2 消息域 | 账号、联系人、会话、消息、来源身份、发送者校准和查询 | 消息查询接口、校准状态、`messageId` / `messageSetRevision`。 |
| M3 上下文 | 人工记忆 CRUD、证据验证、ContextBundle、画像/消息记忆沉淀与重建 | Context / Memory 契约、`contextRevision`、`IContextToolSessionV1`。 |
| M4 Agent | Runtime、persona、建议批次、咨询 Thread/Turn、运行记录 | 建议、咨询及运行 API；`IAgentRuntime` 和受限 Codex CLI executor。 |
| M5 工作区 | Web 授权、会话展示、人设、复制建议、咨询交互 | HTTP API；页面只把建议复制到剪贴板，不调用微信发送。 |
| M6 平台 | 身份过滤、迁移、运行探针、CLI 进程隔离、outbox 投递 | 共用基础设施，不接管业务数据所有权。 |

## 数据库迁移

单个 API migration runner 按 `db/migrations/NNN_*.sql` 名称顺序嵌入和执行，版本登记在 `copilot.schema_migrations`。每个迁移使用事务和校验和；已登记脚本内容不可变更。当前编号为 `001`–`010`。恢复与升级前备份步骤见 [本机开发与运行手册](Local_Operations_Guide.md)。
