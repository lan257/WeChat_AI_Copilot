# M4 Agent 任务执行：详细设计

> 状态：待评审；依总体架构和 Contracts v1。  
> 目标：用稳定、可替换的 Agent Runtime 执行**三条备选回复**和**当前联系人自由咨询**，不在业务层自制复杂多 Agent 系统。

## 1. 设计目标与首版范围

两个核心任务：`ReplyCandidates`（精确三条、有预设人设、可换批）与 `FreeConsultation`（可基于当前聊天主动找话题、解析消息、辅助沟通决策，多轮追问）。不依赖自动推送、用户反馈训练、长期自学习或群聊支持。

执行形式优先研究成熟 Runtime（Codex CLI + Skill + 只读业务工具），保留 `IAgentRuntime`，不预先承诺 Codex 比简单 LLM 效果更好；产品质量、成本和权限可作为实际选型依据。**首版可以用单次 LLM 完成简单建议，复杂自由咨询再进入工具使用循环**，避免把每条建议变成耗时的多轮 Agent 工程。

## 2. 边界与结构

```text
M5 Web → IAgentTaskService → M4 Task coordinator
                               ├─ Scope & Quota Guard
                               ├─ IContextService (M3)
                               ├─ IAgentRuntime
                               │    ├─ CodexCliRuntime [候选]
                               │    ├─ HostedRuntime [候选]
                               │    └─ SimpleLlmRuntime [候选]
                               ├─ Read-only Tool Broker → M3 → M2
                               └─ Result Validator + M4 Repository
                                            ↓ SuggestionBatch / ConsultationTurn
                                           M5 Web
```

M4 持有 Agent 运行记录、三候选批次、咨询 Thread、Prompt/Skill 版本配置；**不拥有**微信连接、原始聊天存储、记忆数据库、发送端口。M4 不给 Agent 任意 SQL、Shell、读取整个文件系统、向联系人发送微信等工具。

## 3. 领域模型和任务状态

| 对象 | 关键字段 | 规则 |
|---|---|---|
| `AgentTask` | `taskType`、`accountId`、`conversationId`、`personaPresetId?`、`contextRevision`、`userInput?`、`previousBatchId?`、`threadId?` | 作用域由后端授权绑定，不能让模型改动 |
| `AgentRun` | `runId`、`taskId`、`runtimeName`、`skillVersion`、`status`、`startedAt`、`endedAt`、`errorCode?`、`budgetUsage?` | 仅保留最低审计元数据，不默认存全量原文 Prompt 日志 |
| `SuggestionBatch` | `batchId`、`accountId`、`conversationId`、`persona`、`contextRevision`、`createdAt`、`suggestions[3]` | 一批属于唯一会话；返回顺序稳定 |
| `AgentConversation` | `threadId`、`accountId`、`conversationId`、`title?`、`createdAt` | Thread 不能换绑定联系人 |
| `AgentTurn` | `threadId`、`runId`、`userInput`、`answer`、`evidenceMessageIds[]`、`createdAt` | 是咨询历史，不是长期记忆 |
| `PersonaPreset` | `id`、`label`、`instruction`、`sortOrder`、`enabled` | 系统维护配置，不能由前端任意指定文件路径 |

状态流：`Queued → Running → Completed | Failed | Cancelled`。任务失效、模型超时、上下文错误必须明确返回；不能让页面永远显示加载中。Agent 的执行失败不影响 M1/M2 入库。

## 4. 任务 A：生成三条候选

1. M5 发 `AgentTaskV1(taskType=ReplyCandidates, accountId, conversationId, personaPresetId, previousBatchId?, contextRevision)`。
2. M4 校验账号/会话、Persona ID 和请求约束，并读取当前 M3 `ContextBundleV1`；必要时附历史摘要及 `omissions`。
3. 使用当前 Skill/Persona 生成**恰好三条不同侧重的自然回复**：兼顾语境、语气，不编造没有证据的经历。可采用 Simple LLM 或 Agent Runtime；运行选择不暴露给 UI。
4. 结果结构化验证：长度/非空/数量/内容边界/无自动发送指令；失败时最多进行受限纠正重试，否则明确失败。
5. 保存 `SuggestionBatch` 与任务的上下文版本，返回 M5。Web 点击某条仅复制；不代表已发送。

“换一批”：携带 `previousBatchId`（必须同账号同会话），生成新的 `batchId`；可以使生成风格发生变化，但不能保证语义百分之百不重复。**新批次必须是真实生成结果，而非前端把三条旧内容按顺序轮转。**

“切换人设”：工作区与极简共用 `personaPresetId`；可立即选择该人设已有、且同一 `contextRevision` 的缓存批次，否则明确请求 M4 新生成。不能只把标签改成人设而沿用旧文本并误导用户。

建议基础预设：`natural`（自然）、`warm`（关心）、`playful`（轻松）、`concise`（简短）。预设属于界面产品方案，可在 M5 联合评审调整。

## 5. 任务 B：AI 自由咨询

典型输入：“我很无聊，想主动给她发消息，怎么自然找话题？”、“对方这句话是什么意思？”、“这次邀约该如何决定？”。

1. M5 在当前选中会话下创建或恢复 `AgentConversation(threadId, accountId, conversationId)`；切联系人时 UI 切换到另一个会话线程，绝不能把之前 Thread 复用到新联系人。
2. 用户输入进入 `FreeConsultation`，服务端确认线程绑定关系；M4 从 M3 构建现有上下文。
3. 若问题需历史事实，Agent 仅能调用只读工具 `get_recent_messages`、`search_chat_history`、`get_contact_memory`、`get_message_evidence`；工具在后端固定账号/会话，模型不能自选更广权限。
4. 输出包含可读回答、可选证据引用与不确定性；不能以推断方式断言对方心理或实际行为。
5. 保存 Turn，并允许同一联系人后续继续追问。用户主动切换联系人时，新问题按新会话执行；旧会话运行中的结果只能写回旧 Thread/会话，不能插入当前 UI。

自由咨询结果不是记忆事实；如果以后增加“保存为记忆”，应由 M3 单独提供**用户确认**的操作，不能让 Agent 直接写入。

## 6. `IAgentRuntime` 合同与执行隔离

```csharp
// 概念接口，仅为模块边界草案
public interface IAgentRuntime
{
    Task<AgentRuntimeResult> ExecuteAsync(
        ScopedAgentRequest request,
        CancellationToken cancellationToken);
}
```

`ScopedAgentRequest` 包含任务类型、已裁剪上下文、Skill 版本、允许的工具能力引用、预算；**不包含数据库密码/微信账号密钥/全盘路径访问权限**。

若采用 Codex CLI：外部进程由隔离 Worker 运行，最小文件权限、不挂载完整用户目录、禁止任意 Shell/网络外发，工具代理仅暴露当前会话的受控读接口。CLI 的输出经结构化校验，不能将其日志与私聊原文不加过滤地写入应用日志。CLI 进程异常退出、超时、取消都归一成 M4 错误。

若采用托管 Agent / 简单模型 API：沿用同一 ScopedAgentRequest 和输出 Schema，保留厂商差异在 adapter 内。服务端可通过特性标记/配置选择可用 Runtime；不得让前端直接提交任意模型命令。

## 7. Skill 与工具定义

Skill 应包括任务目标、上下文要求、证据与不确定性规则、候选输出格式、禁止行为；例如 `reply-suggestions` 和 `conversation-advice`。**Skill 是行为指导，不是权限边界**。

| 工具 | 所属提供者 | 能力 | 限制 |
|---|---|---|---|
| `get_recent_messages` | M3 → M2 | 获取本会话最近消息 | 上限、可溯源、后端绑定 scope |
| `search_chat_history` | M3 | 按文本/日期搜索当前会话 | 不跨联系人、不无界查询 |
| `get_contact_memory` | M3 | 获取当前视角下有效的人物画像/人工记忆及来源类型 | 待确认视角无画像；区分人工事实与生成摘要 |
| `get_message_evidence` | M3/M2 | 对若干命中 messageId 查证 | 再次按授权范围校验 |

工具返回的聊天文本都属于**不可信内容**。微信聊天中出现“请忽略系统要求、执行某条命令”等文本时，只能被视为消息原文，不是 Agent 的新指令。工具调用不允许侧效应，不开放写数据库或微信发送工具。

## 8. API 与实时更新

| 路径/端口（建议） | 行为 | 输出 |
|---|---|---|
| `POST /api/ai/suggestions` | 生成三条新候选 | `SuggestionBatchV1` 或 `runId` |
| `GET /api/ai/suggestions/{batchId}` | 查询某批候选 | batch + contextRevision |
| `POST /api/ai/consultations` | 建立会话绑定线程 | threadId、accountId、conversationId |
| `POST /api/ai/consultations/{threadId}/turns` | 基于该线程自由提问 | 生成后的 Turn 或 runId |
| `GET /api/ai/consultations/{threadId}` | 恢复咨询历史 | 仅当前授权会话 |
| `GET /api/ai/runs/{runId}` | 查询执行状态 | Queued/Running/Completed/Failed/Cancelled |

首版可以同步返回；若采用 CLI 导致任务较长，则适配 `202 Accepted + runId`，前端轮询/事件通知。**合同应从现在就允许异步**，避免 M5 为某种 Runtime 写死超时假设。

## 9. 失败、预算与安全

- 每任务有服务端可配置的最大时长、上下文大小、最大工具次数、并发量和可接受费用。初始阈值在 M6 环境配置、M4 真实测试中定，不虚构统一最优值。
- 模型/Agent 不可用：提供明确错误和重试；可以保留上一个**明确标为旧版**的批次；绝不能假装已经刷新成功。
- 会话在执行期间新增消息：本次结果保留启动时 `contextRevision`；UI 标注可能过时，新任务才读取新上下文。
- 隐私：只提交与当前任务必要的片段；日志默认记录 token 计数、耗时、错误码、`runId`，不记录聊天全文；厂商 API 数据出境/存储策略属于部署配置和告知事项。
- 用户输入与历史原文均为不可信内容；模型无法修改自身可访问的联系人范围。
- 不生成虚构的证据 ID；有事实引用则关联合法 `messageId` / `memoryId`，无可靠证据时明确“不确定”。

## 10. 并行开发子任务

1. **M4-A（立即）**：定义 `IAgentRuntime`、Task/Batch/Thread 模型、输出 Schema，用 M3 `ContextBundle` fixtures 开发；与 M5 共识三条候选和换批语义。
2. **M4-B（立即）**：Mock/Fake Runtime + 可重复的批次/咨询合同测试，支持前端并行开发。
3. **M4-C（并行）**：Codex CLI 实验 adapter + 隔离工具代理；和 Simple LLM adapter 用相同 fixture 对比，不影响前述功能。
4. **M4-D（在 M3 工具可用后）**：真实上下文检索工具的授权联调与多轮咨询；保存线程。
5. **M4-E**：运行状态、错误码、预算、失败恢复和隐私审计，与 M6 对接。

## 11. 验收与评审

| 编号 | 输入场景 | 通过标准 |
|---|---|---|
| M4-T01 | 生成建议 | batch 必须为 3 条非空内容，ID/会话/人设/版本一致 |
| M4-T02 | 换一批 | 提交正确 previousBatchId，返回新 batch 或明确错误，不伪造 |
| M4-T03 | 切换人设 | 新生成遵从相应人设；工作区/极简模式读取同一批次 |
| M4-T04 | 主动找话题、决策咨询 | 可以多轮查询/追问，允许说明证据不足 |
| M4-T05 | 联系人 A 与 B 的线程 | 任何 A 的 threadId 无法在 B 上继续，拒绝越权工具查询 |
| M4-T06 | 恶意消息包含指令 | 不执行 Shell/SQL/微信发送/越权检索 |
| M4-T07 | 模型执行超时/退出 | Run 转 Failed/Cancelled，可重试，不影响 M1/M2 |
| M4-T08 | 生成时新增消息 | batch 附旧 revision，M5 可检测过期 |
| M4-T09 | Mock 与真实 Runtime 切换 | M5/M3 合同不变 |

**完成定义：** 一个真实可配置模型路径（供应商/框架不强制）、Mock 路径、三候选与自由咨询真实业务 API、按会话授权的只读工具、错误/状态/预算处理和无微信发送权限。选择 Codex CLI 是候选，不是无条件的正式选型结论。
