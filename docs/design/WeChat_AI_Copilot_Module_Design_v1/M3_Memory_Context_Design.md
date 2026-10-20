# M3 AI 记忆与上下文：详细设计

> 2026-10-20 实施更新：本文原有的人工校准门控已被《WeChat_Automatic_Sender_Identity_Implementation_Spec.md》取代。记忆只依据持久化的 `SourceConfirmed` 方向；Unknown 消息保留为中性检索信息，不归属到本人或联系人。

> 状态：待评审；以总体架构与 `00_Shared_Contracts.md` 为准。  
> 边界：M3 负责**可信检索、记忆维护、上下文打包**；不拥有微信原始消息、不运行 Agent Loop、不发送微信。

## 1. 目标与最小价值

为一个指定 `accountId + conversationId` 提供：(1) 最近消息上下文；(2) 按关键词/时间窗口检索历史；(3) 可人工增删修订的少量长期记忆；(4) 按 `SourceConfirmed` 方向生成、加载账号/联系人画像摘要及从消息沉淀的长期记忆；(5) 回溯每条可确认事实的证据。自动画像与消息抽取的长期记忆共用来源证据规则、未沉淀文本窗口和批次游标；向量索引、复杂性格推断和事件时间线仍不是基础版硬依赖。

M3 的成功标准不是“自动猜得更准”，而是**让 AI 有办法查到正确历史，又能知道哪些信息没有证据**。手动补录的历史消息进入 M2 后，M3 自动通过查询看到新增历史，不需要单独导入第二份聊天库。

## 2. 模块边界

```text
M2 IMessageQuery / MessageSource (授权、来源、方向证据)
        ↓
M3 HistorySearch + MemoryRepository + ContextBuilder
        ↓ ContextBundleV1 / Read-only Tools
M4 Agent Runtime
        ↓
M5 UI（经授权 API 查看、维护用户记忆）
```

M3 严禁：直接运行微信库 SQL；把 `Unknown` 或未经用户选择的方向推断成确定的本人/对方；把模型对某人的猜测写为客观事实；把 Agent 自由咨询记录当长期记忆自动入库。M3 可依赖 M6 审计/鉴权基础。

## 3. 数据模型

| 逻辑对象 | 字段与行为 | 数据所有者 |
|---|---|---|
| `MemoryItem` | `memoryId`、`accountId`、`subjectType`、`subjectId`、`conversationId?`、`text`、`category?`、`origin`、`status`、`createdAt`、`updatedAt`、`observedAt?`、`sourceMessageIds[]`、`calibrationVersion?`、`sourceMessageCursor?`、`version` | M3 |
| `MemoryEvidence` | 连接记忆与 M2 `messageId`；可带原文片段定位与来源可信等级 | M3 保存引用，不复制/修改 M2 原文 |
| `ParticipantProfileSnapshot` | `subjectType`、`subjectId`、`conversationId`、`summary`、`evidenceMessageIds[]`、`calibrationVersion`、`sourceMessageCursor`、`generatedAt`、`status` | M3 派生快照；与同批 `MessageExtracted` 记忆共享来源游标和视角版本；不覆盖人工 `MemoryItem` |
| `ContextBundle` | `accountId/conversationId/contextRevision`、最近消息、有用记忆、检索证据、`omissions`、预算 | M3 临时构建结果 |
| `SearchResult` | 命中 `messageId`、时间、方向及来源可信度、受限片段 | M3 聚合 M2 查询结果 |

`subjectType` 为 `WechatAccount` 或 `Contact`，`subjectId` 指向该微信账号范围内的稳定参与者身份；当前 Web 登录用户只记录为操作主体，不作为画像主体。一个联系人在不同微信账号下应有独立画像作用域。`origin`：`UserEntered` / `MessageExtracted`；人工记忆在用户操作后立即保存，消息抽取记忆只基于 `SourceConfirmed` 方向生成。`status`：`Active` / `Superseded` / `Deleted` / `NeedsReview`。来源属于人工录入但未挂消息证据时，应标记“用户备注”，不能伪装成“聊天中确认”。

首版数据库表：`memory_items`、`participant_profile_snapshots`、画像/记忆共享的沉淀游标（可并入 M3 状态表）及可选 `memory_evidence`。画像快照和自动 `MessageExtracted` 记忆必须保存生成时的 M2 方向版本与来源消息游标；当前 API 的兼容字段名仍为 `calibrationVersion`。自动画像与自动记忆是可重建投影；用户维护的 `UserEntered` `MemoryItem` 不因新消息方向解析而覆盖，也不要求绑定方向版本。删除记忆不默认删除 M2 中的原始聊天；用户请求彻底删除原始数据时应调用 M2/M6 有权的数据删除流程。

## 4. 检索策略（首版可简单）

### 4.1 最近消息

`BuildContext(accountId, conversationId, anchorMessageId?, recentMessageLimit)` 必须从 M2 获取当前获准的消息页。默认值可建议 30，但必须由服务端限制最高上限；不要接受模型要求的无界读取。明确消息顺序、源时间、方向、校准可信度；发生时间不可用时标注不确定。

### 4.2 历史查询

基础版通过 M2 `IMessageQuery.SearchAsync`，使用会话范围、关键词、时间区间、最多返回条数；返回对应 `messageId` 和来源信息。对于“之前说的那件事”，可以先召回相关原文，再根据命中 ID 扩展邻近消息（应受同一会话权限控制）。搜索不得把跨联系人结果混合。

查询结果为空有几种可能：从未提过、尚未补录、源消息未覆盖、检索条件过窄。工具应在 `omissions` 标记“历史覆盖未知”，不能简单断言“从未聊过”。

### 4.3 长期记忆

人工录入的 `UserEntered` `MemoryItem` 由用户操作后立即保存；消息抽取的 `MessageExtracted` 长期记忆则受 4.5 中与画像共用的确认门控、滑动窗口、视角版本和游标控制。两种来源都附 `origin`、证据 ID 和更新时间，可按当前会话读取。自动抽取只可从明确证据生成；来源不足或推断不确定时写 `NeedsReview` 并排除出确定事实上下文，不得覆盖、合并掉或改写人工记忆。

### 4.4 上下文预算

优先保留：最新聊天与用户问题 → 证据匹配历史 → 可信且相关的人工/消息抽取长期记忆 → 当前方向版本的参与者画像 → 其它摘要。去除重复片段并截断，`ContextBundle.omissions` 记录超预算被排除的部分。上下文超预算不意味着无限制增加 Token；由 M3 约束交给 M4 的可见窗口。

### 4.5 人物画像何时加载和更新

- **加载：** 不因打开会话页面就调用模型。每次生成建议或开始 AI 咨询时，`ContextBuilder` 加载当前账号/会话下符合方向证据规则的记忆与画像；Unknown 消息只能以 Unknown 标签用于中性检索，不能提供本人或联系人风格证据。
- **账号身份就绪：** 服务端将账号目录绑定的 `account_wxid` 作为方向判断基准。API 旧字段 `PerspectiveState=Confirmed` 表示账号 wxid 已绑定；`WaitingForConfirmation` 仅为兼容状态，实际表示账号身份不可用，不表示需要用户逐会话确认。
- **首次沉淀：** 首条账号身份已绑定且有方向证据的消息进入 `message.created` 后，M3 捕获当前消息高水位；只使用方向和证据为 `SourceConfirmed` 的 Direct/Text 消息。没有可用文本时建立起始游标并进入累积状态。
- **后续滑动窗口：** 按 `accountId + conversationId` 独立累计尚未沉淀的 SourceConfirmed 文本；画像和自动长期记忆共用窗口与游标。文本先将 CRLF/CR 规范为 LF，再按 Unicode 码点计数（不裁剪首尾空格）；严格超过 5,000 字符时处理窗口内全部新文本并推进共享游标。
- **失败与幂等：** 生成或持久化失败时不推进游标、不清空窗口，按同一来源范围重试；画像、抽取记忆和游标在一个原子数据库提交中一起落地，以源版本与 `sourceMessageCursor` 幂等写入。相同作用域最多运行一个沉淀批次；状态不得显示为最新。
- **数据范围：** 仅使用 M2 当前已保存的消息，不因沉淀自动启动历史补录或扩大扫描。历史不完整时标注实际覆盖范围。
- **身份补解析：** 若旧 Unknown 消息后续从源库读到分片内 sender wxid，M2 更新已持久化方向和消息修订，并发布 `message.identity-resolved`；M3 重新读取当前持久化方向。无法解析的消息仍排除在参与者画像和自动消息记忆之外。
- 页面可显示现有状态名，但不得显示“确认方向”或“互换身份”操作。未知来源不推断心理/人格结论。
## 5. 方向、证据与记忆可靠性

- 画像主体是微信参与者：当前采集账号和会话联系人分别建档，不是当前 Web 操作用户。
- 只有持久化的 `SourceConfirmed` 消息方向可以分配给账号本人或联系人，并作为对应参与者的语言风格证据。`UserConfirmed` 等旧校准证据已迁移为 Unknown，不是可接受的新证据。
- `Unknown` 消息可在上下文中保持 Unknown 用于中性检索；不得假设为本人，也不得用于本人/联系人的画像与消息抽取记忆。
- 对方明确陈述可作为来源事实；AI 解读语气和心理仍只是解释，不自动归档为确定关系特征。
- 每条记忆展示创建来源、关联的原始 `messageId` 与人工更正版本。引用消息被删除时，引用失效或显示已删除，不能继续输出缓存的隐私片段。
## 6. ContextRevision 定义

M3 生成不透明 `contextRevision`；至少受以下变化影响：该会话 M2 消息追加、已有消息方向/发送者身份由 Unknown 更新为 SourceConfirmed、M3 Active 记忆版本变更、账号/联系人画像快照版本变更。该修订可用摘要哈希或内部单调版本实现，不要求把字段暴露给 Web。M4 的 SuggestionBatch 回传任务启动时的 revision；M5 对比当前 revision 提示“建议可能基于旧上下文”。账号 wxid 未绑定时，返回兼容状态 `profileStatus=WaitingForConfirmation` 且不包含画像快照。

**隔离要求：** contextRevision 必须绑定当前账号/会话；不能把全站最后一条消息 cursor 当成此会话的唯一版本来源。历史补录后即使最晚消息没改变，记忆可见历史也发生变化，因此应提升上下文版本。

## 7. 对外接口和工具契约（D05/D06）

| 接口 | 输入 | 输出 | 设计约束 |
|---|---|---|---|
| `IContextService.BuildAsync` | ContextRequestV1 + 后端主体 | ContextBundleV1 | 当前会话权限、方向可信等级、omissions |
| `IHistorySearch.SearchAsync` | 账号+会话+query+时间+limit | `SearchHit[]` | 不跨范围，命中附 messageId |
| `IHistorySearch.GetEvidenceAsync` | messageIds + 授权上下文 | 受限引用消息 | 越权消息全部拒绝 |
| `IMemoryService.ListAsync` | 会话+过滤 | `MemoryItem[]` | 用户只能读取当前获准联系人 |
| `IMemoryService.Create/Update/Delete` | 会话+内容+version | 当前记录/新版本 | 乐观版本、操作审计 |
| `IParticipantProfileService.GetStatusAsync` | 账号+会话+授权主体 | 两方画像/记忆沉淀状态、待沉淀字符数、游标和版本 | 账号 wxid 未绑定时仅返回兼容状态 `WaitingForConfirmation`；不暴露强制刷新命令 |
| M2 `message.created` / `message.identity-resolved` 消费者 | 已持久化消息或身份解析事件 | 内部按阈值排队沉淀或重读方向变化 | 同一来源版本和高水位幂等；失败保留游标 |
| `get_recent_messages`（M4 业务工具） | 不接受任意 account scope | 最近消息 | 后端从 Task 绑定范围注入 |
| `search_chat_history`（M4 业务工具） | query + 可选时间/limit | 证据列表 | 服务端固定会话 |
| `get_contact_memory`（M4 业务工具） | filter/limit | 可信记忆 | 不返回不属于当前会话的资料 |
| `get_message_evidence`（M4 业务工具） | 来源 messageIds | 最小必要片段 | 不返回后台凭据/数据库连接信息 |

M3 业务接口可以是进程内服务或经 M6 授权的 API；不为 M4 单独公开无认证的 MCP endpoint。若使用 Codex CLI，工具代理需要将任务作用域封装在服务端闭包/会话授权中，不依赖模型正确填写 scope。

## 8. 用户操作与业务流程

### 创建/编辑记忆

工作区选中账号 A/会话 B → M5 提交人工记忆 → M3 校验会话授权、文字长度、来源是 `UserEntered` → 立即插入带版本记录（人工记忆不进入消息沉淀窗口）→ `contextRevision` 递增 → 工作区显示记录与编辑能力。修改/删除记忆仅影响 M3，不改动聊天原文。

### Agent 查询历史

M4 收到咨询（例如“我想主动找话题”）→ M3 构建最近消息 → 若需要更多证据，M4 调用 `search_chat_history` → M3 调用 M2 的查询 API → 返回带 `messageId` 的片段 → M4 推理。若同一时间用户切换到别的联系人，已启动的任务仍只能访问启动时绑定的 B。

### 历史补录后的刷新

M2 补入旧消息 → 发布“该会话消息集已变化”事件（不必携正文）→ M3 contextRevision 变化 → 下一次咨询/建议纳入新增历史；对已生成旧建议仅标注过时，不凭空重写。

### 账号 wxid 与画像/记忆沉淀

Reader 校验所选账号目录的 wxid → M2 将 wxid 绑定至账号 → `message.created` 触发 M3 首次处理持久化为 `SourceConfirmed` 的文本 → 后续未沉淀文本按账号/会话共用窗口累计，严格超过 5,000 字符时以高水位一次性沉淀并推进共享游标 → 既有 Unknown 消息若后来解析成功，由 `message.identity-resolved` 触发重读 → 下一次 AI 请求加载当前持久化方向对应的最新产物。未知消息仍保持中性，不能用作任一参与者的画像证据。

## 9. 并行开发建议

- M3-A：先用稳定 fixtures 编写 `ContextBundleV1` 构建与预算裁剪，**不等 M2 真库**。
- M3-B：`MemoryItem` 持久化、人工维护 CRUD、证据引用验证；数据库 schema 的表所有权归 M3，迁移命名与 M6 协调。
- M3-C：封装 M2 只读 `IMessageQuery`，先 Mock 再真实适配；优先实现关键词与时间检索，暂不依赖 pgvector。
- M3-D：ContextRevision 与方向可信等级、缺失/截断标注；与 M4 Mock 工具对接。
- M3-E：实现账号 wxid 就绪检查、SourceConfirmed 首次沉淀、画像与消息抽取长期记忆共用的 >5,000 字滑动窗口、共享来源游标/幂等状态和身份解析更新后的重读；不实现会话级确认或定时刷新。

## 10. 质量门槛和测试

| 场景 | 期望 |
|---|---|
| 同一账号的会话 A/B 同时有记忆 | A 的查询、咨询不返回 B 的任一记忆/消息 |
| 会话历史不完整或检索零命中 | 返回有限结果并说明覆盖不确定，不捏造“从未提过” |
| 一条消息方向为 Unknown | 仍可作事实记录，不用作确定“本人语言风格”依据 |
| 账号 wxid 未绑定 | 方向保持 Unknown；不创建、更新或加载任一参与者画像 |
| 存在 Unknown 消息 | 可用于中性检索，不分配到本人或联系人 |
| 首条 SourceConfirmed 消息持久化 | 事件触发首次画像/长期记忆沉淀；空文本则建立起始游标 |
| 已确认后再次互换预览 | 既有映射及同版本产物保持有效 |
| 已确认后提交新身份 | 旧版本画像和消息抽取记忆退出 AI Context；按新版本重建，人工记忆保持不变 |
| 累积 5,000 个未沉淀字符 | 不触发；沉淀状态继续累计 |
| 累积 5,001 个未沉淀字符 | 一次性沉淀高水位以内文本并推进画像/长期记忆共享游标；其后消息进入新窗口 |
| 沉淀过程失败 | 不推进游标、不丢窗口；同范围重试幂等，状态显示失败/待重试 |
| 沉淀批次后发起建议/咨询 | 加载匹配当前方向版本的最近成功画像/记忆并标示当前未沉淀字符数 |
| 用户新增/修改/删除一条记忆 | revision 变化，旧消息保持不变 |
| 旧消息被历史补录插入 | 下一次上下文可检索到，来源 `messageId` 可追踪 |
| Agent 工具请求越权 ID | 后端拒绝，不泄露目标是否存在 |
| 查询返回过多原文 | 有上限、有裁剪标记、不把全聊天打包给模型 |
| 用户删原文后证据引用失效 | 不继续返回已删除原文缓存 |

**完成定义：** 对当前授权会话提供可查证、可维护的上下文、人工记忆和 SourceConfirmed 方向下的人物画像；账号 wxid 就绪后由首条可信消息启动首批沉淀，之后画像与消息抽取的长期记忆按同一 >5,000 字窗口增量沉淀、失败可幂等重试，并能用 Mock/真实 M2 合同测试。不存在逐会话方向确认或定时更新。复杂人格推断、事件图谱和嵌入向量检索属于后续增强。
