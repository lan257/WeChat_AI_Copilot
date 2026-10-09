# M3 AI 记忆与上下文：详细设计

> 状态：待评审；以总体架构与 `00_Shared_Contracts.md` 为准。  
> 边界：M3 负责**可信检索、记忆维护、上下文打包**；不拥有微信原始消息、不运行 Agent Loop、不发送微信。

## 1. 目标与最小价值

为一个指定 `accountId + conversationId` 提供：(1) 最近消息上下文；(2) 按关键词/时间窗口检索历史；(3) 可人工增删修订的少量长期记忆；(4) 按已确认的微信账号视角生成、加载账号/联系人画像摘要及从消息沉淀的长期记忆；(5) 回溯每条可确认事实的证据。自动画像与消息抽取的长期记忆共用确认门槛、未沉淀文本窗口和批次游标；向量索引、复杂性格推断和事件时间线仍不是基础版硬依赖。

M3 的成功标准不是“自动猜得更准”，而是**让 AI 有办法查到正确历史，又能知道哪些信息没有证据**。手动补录的历史消息进入 M2 后，M3 自动通过查询看到新增历史，不需要单独导入第二份聊天库。

## 2. 模块边界

```text
M2 IMessageQuery / SenderCalibrationView (授权、来源、方向证据)
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

`subjectType` 为 `WechatAccount` 或 `Contact`，`subjectId` 指向该微信账号范围内的稳定参与者身份；当前 Web 登录用户只记录为操作主体，不作为画像主体。一个联系人在不同微信账号下应有独立画像作用域。`origin`：`UserEntered` / `MessageExtracted`；人工记忆在用户操作后立即保存，消息抽取记忆只在已确认视角的首次/阈值批次中生成。`status`：`Active` / `Superseded` / `Deleted` / `NeedsReview`。来源属于人工录入但未挂消息证据时，应标记“用户备注”，不能伪装成“聊天中确认”。

首版数据库表：`memory_items`、`participant_profile_snapshots`、画像/记忆共享的沉淀游标（可并入 M3 状态表）及可选 `memory_evidence`。画像快照和自动 `MessageExtracted` 记忆必须保存生成时的 M2 `calibrationVersion` 与来源消息游标；版本不等于当前已确认视角时不可加载。自动画像与自动记忆是可重建投影；用户维护的 `UserEntered` `MemoryItem` 不因自动沉淀或互换身份而覆盖，也不要求绑定方向版本。删除记忆不默认删除 M2 中的原始聊天；用户请求彻底删除原始数据时应调用 M2/M6 有权的数据删除流程。

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

- **加载：** 不因打开会话页面就调用模型。每次生成三条建议或开始 AI 咨询时，`ContextBuilder` 自动取该账号/会话下最新成功且 `calibrationVersion` 匹配的账号本人画像、联系人画像、人工记忆及当前视角有效的消息抽取长期记忆，与近期消息一起装入本次 `ContextBundle`；不要求用户手动点“加载画像”。画像和自动记忆带来源游标/更新时间，M4 可知其是否落后于最新消息。处于 `Accumulating`/`Consolidating`/`Failed` 时，可继续提供同一 `calibrationVersion` 下最近一次成功快照，但必须返回其来源游标和当前状态；没有同版本成功快照则不提供派生画像/自动记忆。不得回退到旧校准版本。
- **初始化门槛：** M2 的小 ID 默认映射处于 `PendingConfirmation` 时，画像和消息抽取的长期记忆状态为“等待确认”。不创建、更新或向 M4 提供任何参与者画像/自动记忆；最近消息仍可检索，但方向标记为临时，不作角色风格证据。用户可先点“互换身份”预览另一候选，预览不写入校准、不启动沉淀；只有随后点“确认身份”，M2 保存 `UserConfirmed` 视角，M3 才开始该会话第一次画像与自动长期记忆沉淀。
- **首次沉淀：** 首次确认后，M3 捕获当前 M2 消息高水位，对其以内已持久化且方向已确认的 Direct/Text 消息执行一次初始批次，不等待 5,000 字阈值；没有可用文本时建立起始游标并进入累积状态。画像摘要与可追溯的 `MessageExtracted` 长期记忆使用同一批输入、同一 `calibrationVersion` 和来源游标提交。
- **后续滑动窗口：** 按 `accountId + conversationId + identityNamespace + calibrationVersion` 独立累计尚未沉淀文本，画像与自动长期记忆共用同一窗口和游标。只计方向已确认的 Direct/Text 消息；计数以持久化文本为准，将 CRLF/CR 统一为 LF 后按 Unicode 码点计数，不裁剪首尾空格，不计字节、Token、媒体、Unknown 或重复消息。待沉淀文本累计**严格大于 5,000 字符**时，M3 捕获当前高水位游标，对游标以内所有尚未沉淀文本一次性生成相关参与者的画像增量及消息抽取长期记忆；成功后以一个逻辑提交更新两类产物并推进共享游标。越过高水位的新消息属于下一窗口，窗口起点随已沉淀游标向前滑动并重新从零累积。5000 整不触发，5001 才触发。
- **失败与幂等：** 生成或持久化失败时不推进游标、不清空窗口，按同一来源范围重试；画像、抽取记忆和游标在一个原子数据库提交中一起落地，以 `calibrationVersion + sourceMessageCursor` 幂等写入，避免出现画像已更新而记忆/游标未更新。相同作用域最多运行一个沉淀批次。状态不得显示为最新。M3 可以在新消息事件到达后检查阈值并排入一次工作，不使用日历定时任务，也不提供手动强制刷新入口。
- **数据范围：** 仅使用 M2 当前已保存的消息，不因沉淀自动启动历史补录或扩大全局扫描。确认身份后的首次批次/换视角重建可分页、有界处理当前已存消息，并保存真实覆盖范围；历史不完整时标注边界。
- **确认新视角后重建：** 用户先互换预览、再确认新视角后，M2 增加 `calibrationVersion`；旧版本画像与消息抽取记忆立即失效并从 AI 上下文排除，M3 按新方向从当前已存消息重建，不受 5,000 字阈值约束。重建期间只允许使用当前消息、人工输入记忆和匹配新版本的产物；人工 `UserEntered` 记忆保留。旧任务完成结果不得覆盖新版本。
- **证据与编辑：** 自动画像摘要和自动长期记忆只基于确认方向的文本，保留来源消息 ID、覆盖游标、视角版本和生成时间。自动生成不覆盖人工记忆；事实不足、来源冲突或推断不确定时写明未知，不生成心理诊断/确定性人格结论。
- 页面展示状态：`WaitingForConfirmation`、`FirstConsolidation`、`Accumulating`、`Consolidating`、`Rebuilding`、`Current`、`Failed`；累积状态显示当前会话作用域共享的待沉淀字符数和 `5,000` 阈值，不显示定时刷新/手动刷新入口。

## 5. 方向、证据与记忆可靠性

- 画像沉淀对象是微信参与者：被采集的微信登录账号与会话联系人分别建档；它不是当前操作 Web 页面用户的个人画像。
- M2 初始化时会将有效范围内较小的数字 raw sender 记录为 `PendingConfirmation` 默认本人；“互换身份”只切换未提交预览，只有用户“确认身份”后，M3 才可将消息归到账号本人/联系人并执行首次画像与长期记忆沉淀。
- 只有 `UserConfirmed` 视角下的本人消息才能作为本人表达风格证据；默认临时方向不得更新任何一方画像。
- 未确认、身份冲突或方向为 `Unknown` 时，消息仍可用于中性会话检索，但不得分配到任一参与者的语言风格画像。
- 对方的明确陈述可以作为来源事实；AI 解读语气和心理则只是解释，不自动归档为确定关系特征。
- 一条记忆应可展示创建来源、关联的原始 `messageId`、人工更正版本。引用消息被用户删除时，引用须失效或显示已删除，不能继续输出缓存的隐私片段。

## 6. ContextRevision 定义

M3 生成不透明 `contextRevision`；至少受以下变化影响：该会话 M2 消息追加、相关 sender 视角版本变更、M3 Active 记忆版本变更、账号/联系人画像快照版本变更。该修订可用摘要哈希或内部单调版本实现，不要求把字段暴露给 Web。M4 的 SuggestionBatch 回传任务启动时的 revision；M5 对比当前 revision 提示“建议可能基于旧上下文”。视角尚待用户确认时，返回 `profileStatus=WaitingForConfirmation` 且不包含画像快照。

**隔离要求：** contextRevision 必须绑定当前账号/会话；不能把全站最后一条消息 cursor 当成此会话的唯一版本来源。历史补录后即使最晚消息没改变，记忆可见历史也发生变化，因此应提升上下文版本。

## 7. 对外接口和工具契约（D05/D06）

| 接口 | 输入 | 输出 | 设计约束 |
|---|---|---|---|
| `IContextService.BuildAsync` | ContextRequestV1 + 后端主体 | ContextBundleV1 | 当前会话权限、方向可信等级、omissions |
| `IHistorySearch.SearchAsync` | 账号+会话+query+时间+limit | `SearchHit[]` | 不跨范围，命中附 messageId |
| `IHistorySearch.GetEvidenceAsync` | messageIds + 授权上下文 | 受限引用消息 | 越权消息全部拒绝 |
| `IMemoryService.ListAsync` | 会话+过滤 | `MemoryItem[]` | 用户只能读取当前获准联系人 |
| `IMemoryService.Create/Update/Delete` | 会话+内容+version | 当前记录/新版本 | 乐观版本、操作审计 |
| `IParticipantProfileService.GetStatusAsync` | 账号+会话+授权主体 | 两方画像/记忆沉淀状态、待沉淀字符数、游标和版本 | 未确认时仅返回 `WaitingForConfirmation`；不暴露强制刷新命令 |
| M2 `message.created` / `conversation.perspective-confirmed` 消费者 | 已持久化消息事件/已确认视角事件 | 内部按阈值排队沉淀或首次构建 | 同一版本和高水位幂等；失败保留游标 |
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

### 确认视角与画像/记忆沉淀

初始化默认小 ID → 等待用户确认 → 用户可互换预览 → 用户确认当前候选 → M2 发布新 `calibrationVersion` → M3 对已存且已确认方向的文本执行首次画像/长期记忆沉淀 → 后续未沉淀文本按账号/会话/namespace/版本共用窗口累计，严格超过 5,000 字符时以高水位一次性沉淀并推进共享游标 → 下一次 AI 请求加载同一已确认版本的最新产物。新消息事件只负责检查阈值，不设日历定时任务或手动强制刷新。若方向再次变更，确认后旧版本的画像和消息抽取记忆立即不可用，M3 按新方向全量重建；重建完成前只向 AI 提供当前消息和独立人工记忆。

## 9. 并行开发建议

- M3-A：先用稳定 fixtures 编写 `ContextBundleV1` 构建与预算裁剪，**不等 M2 真库**。
- M3-B：`MemoryItem` 持久化、人工维护 CRUD、证据引用验证；数据库 schema 的表所有权归 M3，迁移命名与 M6 协调。
- M3-C：封装 M2 只读 `IMessageQuery`，先 Mock 再真实适配；优先实现关键词与时间检索，暂不依赖 pgvector。
- M3-D：ContextRevision 与方向可信等级、缺失/截断标注；与 M4 Mock 工具对接。
- M3-E：实现“确认身份”门控、首次沉淀、画像与消息抽取长期记忆共用的 >5,000 字滑动窗口、共享来源游标/幂等状态和换视角后的版本化重建；不实现定时任务或手动强制刷新。

## 10. 质量门槛和测试

| 场景 | 期望 |
|---|---|
| 同一账号的会话 A/B 同时有记忆 | A 的查询、咨询不返回 B 的任一记忆/消息 |
| 会话历史不完整或检索零命中 | 返回有限结果并说明覆盖不确定，不捏造“从未提过” |
| 一条消息方向为 Unknown | 仍可作事实记录，不用作确定“本人语言风格”依据 |
| 小 ID 默认已记录但用户尚未确认 | 可显示临时方向；不创建、更新或加载任一人物画像 |
| 只点“互换身份” | 仅预览变化，不持久化，不触发画像或长期记忆沉淀 |
| 首次“确认身份” | 不等待字符阈值，立即对已存文本执行首次画像/长期记忆沉淀；空文本则建立起始游标 |
| 已确认后再次互换预览 | 既有映射及同版本产物保持有效 |
| 已确认后提交新身份 | 旧版本画像和消息抽取记忆退出 AI Context；按新版本重建，人工记忆保持不变 |
| 累积 5,000 个未沉淀字符 | 不触发；沉淀状态继续累计 |
| 累积 5,001 个未沉淀字符 | 一次性沉淀高水位以内文本并推进画像/长期记忆共享游标；其后消息进入新窗口 |
| 沉淀过程失败 | 不推进游标、不丢窗口；同范围重试幂等，状态显示失败/待重试 |
| 沉淀批次后发起建议/咨询 | 加载匹配当前 `calibrationVersion` 的最近成功画像/记忆并标示当前未沉淀字符数 |
| 用户新增/修改/删除一条记忆 | revision 变化，旧消息保持不变 |
| 旧消息被历史补录插入 | 下一次上下文可检索到，来源 `messageId` 可追踪 |
| Agent 工具请求越权 ID | 后端拒绝，不泄露目标是否存在 |
| 查询返回过多原文 | 有上限、有裁剪标记、不把全聊天打包给模型 |
| 用户删原文后证据引用失效 | 不继续返回已删除原文缓存 |

**完成定义：** 对当前授权会话提供可查证、可维护的上下文、人工记忆和确认视角下的人物画像；首次确认立即启动首批沉淀，之后画像与消息抽取的长期记忆按同一 >5,000 字窗口增量沉淀、失败可幂等重试、换视角确认后重建，并能用 Mock/真实 M2 合同测试。不存在定时更新或手动强制刷新。复杂人格推断、事件图谱和嵌入向量检索属于后续增强。
