# M2 账号、会话与消息管理：详细设计

> 详细设计状态：待独立评审；总体架构 v1.0-Draft 的具体化，不代表代码均已实现。  
> 优先级：P0／**最早实施的核心模块**；负责人需与 M1、M3、M5 签署接口契约。  
> 唯一业务责任：`ChatAccount / Contact / Conversation / Message / MessageSource / SenderCalibration`；历史与实时消息在此合流。

## 1. 目标和范围

首版完成单用户、一个 Windows 微信账号下，指定普通一对一会话的消息归档、来源审计、只读查询、方向投影与人工视角设置、增量刷新游标，并为历史补录提供可幂等的统一入口。可创建多个会话记录，但不能把“多会话目录”误认为“多个会话同时在采集”。

明确不做：微信源数据库查询、AI 记忆抽取、Agent 执行、微信自动发送、群消息精细归属、跨 shard 未证实的自动合并。依赖 M1 提供来源原始字段，依赖 M6 的鉴权/数据库迁移基础。

## 2. 核心领域对象及建议表结构

> 以下为**逻辑数据模型建议**，实际迁移由模块所有权审核后定稿；与现有 POC `chat_messages` 迁移必须显式兼容，不要直接丢弃重建。

| 逻辑对象/表 | 最小字段与约束 | 说明 |
|---|---|---|
| `ChatAccount` / `chat_accounts` | `id`、`provider`、`external_account_id`、`display_name`、`created_at`；唯一 `(provider, external_account_id)` | 登录授权允许的账号；不凭昵称归并 |
| `Contact` / `contacts` | `id`、`account_id`、`external_contact_id`、`display_name`；唯一 `(account_id, external_contact_id)` | 显示名允许修改，不改变身份 |
| `Conversation` / `conversations` | `id`、`account_id`、`contact_id`、`external_conversation_id`、`kind=Direct`、`last_message_cursor`；唯一 `(account_id, external_conversation_id)` | 同账号中的会话唯一，属于一个账号 |
| `Message` / `chat_messages` | `id`、`account_id`、`conversation_id`、`message_type`、`text_content`、`source_occurred_at`、`ingested_at`、`direction`、`direction_evidence`、`external_message_id` | 内部 ID 永远不随来源更改；时间必须区分“发生”与“入库” |
| `MessageSource` / `message_sources` | `id`、`message_id`、`provider`、`source_kind`、`live_external_message_id`、`server_id?`、`local_id?`、`sort_seq?`、`shard_fingerprint?`、`message_table?`、`raw_sender_id?`、`identity_namespace?`、`job_id?` | 一条 Message 可以有实时/历史多份来源证据 |
| `SenderCalibration` / `sender_calibrations` | `id`、`account_id`、`conversation_id`、`identity_namespace`、`account_self_raw_id`、`contact_raw_id`、`version`、`evidence`、`confirmed_at` | 保存用户为目标微信账号选择的本人视角；有效作用域内恰好两方时生效 |
| `ImportReview` / `import_reviews` | `id`、`job_id`、`source_ref`、`reason_code`、`status` | 来源键冲突、范围不明、字段缺失，不能静默吞掉 |

关键 DB 约束：保留 POC 已验证的 `(provider, account_id, external_message_id)` 有效来源键的唯一性；新 `MessageSource` 使用独立的来源证据索引和必要的部分唯一约束，**不能对未证实全域唯一的 `server_id` 做全局唯一**。所有 `Message`/`MessageSource` 通过 `account_id` 和所属 `conversation_id` 受控访问。要保证外键链证明会话属于账号，而不只是靠控制器输入检查。

建议设计 source-identity 映射映射版本 `matchingStrategyVersion`（起始如 `wx4-legacy`），以便未来在不破坏旧键的情况下升级跨 shard 的稳定身份策略。

## 3. 消息写入算法

```text
Ingest(sourceEvent):
  1) 鉴别 M1 来源身份、协议版本、账号与目标会话是否获准
  2) 验证字段、归一化账号/会话作用域；拒绝未授权来源
  3) 保留原始 localId/sortSeq/serverId/shard/sender 等证据
  4) 在已验证范围复算现有 wx4: 键；检查碰撞/跨 shard 风险
  5) 精确匹配：复用已有 Message，新增/关联 MessageSource
  6) 可信新键且无碰撞：创建 Message + MessageSource
  7) 身份不可证或冲突：NeedsReview，不用正文或时间猜测
  8) 以校准的有效版本计算方向与 evidence；不覆写更可信证据
  9) 同一事务内提交，返回内部 messageId 和查询 cursor
 10) 提交成功后发布轻量 message.created 通知
```

**幂等与并发：** 多请求竞争以 PostgreSQL 约束 + 事务/`INSERT ... ON CONFLICT` 为最终裁定，不能只先查后插。`MessageSource` 的已存在情况不会重复创建业务消息。处理同一批次内重复来源记录也应该幂等，不能只依赖 HTTP 去重。

**时间与排序：** 事件没有合法来源时间时仍保存 `ingested_at`，UI 明示时间来源；分页排序使用稳定组合（如 `source_occurred_at NULLS LAST`、`ingested_at`、`messageId`），对 UI 输出不透明游标，前后页顺序稳定。不要把时间戳当消息唯一 ID。

**文本与非文本：** 仅 `Text` 进入首版主流程；未知类型可以记录基本类型和来源证据但不伪造正文；返回清晰“不支持显示”的状态。

## 4. 历史补录对账

- M1 决定扫描哪些本机 shard 并报告实际覆盖范围；M2 负责逐条 `Match/Create/NeedsReview`，记录每条来源证据与 job 引用；不要求 M2 访问本地 SQLite。
- 实测键：`wx4:` + SHA-256(`account|conversation|local_id|sort_seq`)，单会话/单 shard 下历史对账实时 5/5。**保持输入格式与 POC 字节级一致**；字段的类型和大小转换需要回归样例。
- 若当前 shard 范围不确定，不能把相同 `local_id`+`sort_seq` 当跨 shard 唯一；先尝试在同一已验证身份范围精确匹配，冲突转 `NeedsReview`。`server_id` 为额外证据而非全球唯一键。
- 历史行与已存在实时消息确认为同一条：新增 `MessageSource`，不重写旧消息正文，不自动把旧 `Unknown` 覆盖成猜测方向。
- **历史补录只增/关联，不基于源端缺失删除**。用户显式数据删除是另一条独立操作。
- 任务计数标准：`read`、`matched`、`created`、`needsReview`、`ignoredUnsupported`、`failed`；注意 `read` 不是完整度保证。

## 5. 原始 sender ID 与微信账号视角选择

### 5.1 作用域与待确认默认值

视角键至少为 `accountId + conversationId + identityNamespace`；本机读取器还需包含 `messageTable + shardFingerprint`。sender ID 只能在同一有效范围内比较，不能把一个账号的规则套到另一个账号。

当 Direct 会话在该范围内恰好出现两个不同、可无损解析为非负十进制整数的 raw sender ID，初始化时自动记录**较小 ID 为本人**。该候选标记为 `PendingConfirmation` / `DefaultSmallSenderId`，并按此临时方向投影已有和新消息供界面展示。只允许数字值比较，不做字符串字典序比较。ID 不可比较、相同、身份候选冲突或会话范围不唯一时保持 `Unknown`。

这条默认规则不是可靠身份事实：实时 POC 中原账号本人 sender ID 为 `1`、对端为 `73`，但测试好友账号本人为 `2`、对端为 `1`。因此工作区必须明确标注“默认/待确认”，并把“互换身份”和“确认身份”设计为两个独立操作。在用户确认之前，M3 不建立或更新账号/联系人的画像或消息抽取长期记忆，M4 也不得把临时方向当作两方表达风格的证据。

### 5.2 操作流程

1. M5 请求 `SenderCalibrationView`，显示候选 ID、默认较小 ID、待确认状态和 `calibrationVersion`。
2. 用户点“互换身份”时，M5 仅切换当前待确认的预览候选，明确显示本人/好友两侧将如何互换；该操作不持久化校准、不重投影消息、不触发画像或记忆沉淀。可重复点击以切回。
3. 用户点“确认身份”后，前端提交明确的 `accountSelfRawSenderId`，而不是可因网络重试反复翻转的 toggle。M2 核验账号/会话/namespace、sender 候选和 `expectedCalibrationVersion`；事务内持久化用户确认映射、`UserConfirmed` 版本和最小审计记录。同一命令重试幂等，竞争更新返回 `409`。
4. M2 按新视角重新投影受影响消息并递增 `calibrationVersion`（画像依赖的视角版本）；不修改 `rawSenderId` 或源消息 ID，并发布已确认视角事件。
5. 只有确认成功后，M3 才为当前账号和联系人执行首次画像/长期记忆沉淀。以后用户再次“互换身份”仍只是预览；再次点“确认身份”才提交新视角。确认后的新视角会使旧视角派生内容失效并按新方向重建；人工维护的 `MemoryItem` 不被改写或删除。
6. 通知 Web/M3 更新方向、画像状态和上下文版本；身份候选冲突时保持 `Unknown`，画像及消息抽取记忆沉淀暂停。

视角设置采用显式值而非“交换”命令：用户再次选择同一 sender ID 不会翻转身份；如需更改，提交新的选择和期望版本。这样浏览器重试不会意外改变方向。

## 6. 对外端口与接口（建议）

| 端口/接口 | 调用方 | 输入 / 输出 | 失败语义 |
|---|---|---|---|
| `IMessageIngestion.IngestAsync` | M1 | `RawMessageEventV1 → IngestReceipt` | schema 错误/账号不允许/来源冲突/后端不可用 |
| `IMessageIngestion.IngestHistoryBatchAsync` | M1 | `HistoryBatchV1 → BatchReceipt` | 不确定行独立记录；部分失败不隐瞒 |
| `IConversationQuery.ListAsync` | M5、M3 | 账号范围 → 当前授权的会话目录 | 越权拒绝 |
| `IMessageQuery.GetPageAsync` | M5、M3 | 账号+会话+游标+limit → 消息页 | 非法游标/越权/会话不存在 |
| `IMessageQuery.SearchAsync` | M3 | 账号+会话+关键词/时间范围 → 引用结果 | 查询范围截断须标记 |
| `ISenderCalibration.GetAsync` | M5 | 会话+namespace → 校准视图 | Sender 数量不足返回不可校准 |
| `ISenderCalibration.SetWeChatAccountPerspectiveAsync` | M5 | 账号本人 raw sender ID + 版本 + 幂等键 → 新视角与版本 | `409` 冲突，`422` 两方前提不满足 |
| `message.created`/`conversation.perspective-changed`/`conversation.perspective-confirmed` | M5、M3 | account+conversation+cursor/version | 不携带正文的轻量通知 |

REST 路径由共享 OpenAPI 定稿；示例：`GET /api/conversations/{id}/messages`、`PUT /api/conversations/{id}/sender-perspective`。内部代码直接模块接口即可，不为了六个模块建六个 HTTP 服务。

## 7. 与其它模块的约定

- **M1 → M2（D01/D02）**：M1 提供原始字段、数据覆盖状态和批次边界；M2 确定归属/幂等并返回处理回执。
- **M2 → M3（D05）**：带账号/会话授权上下文的消息查询与方向证据；M3 不访问 `chat_messages` 直接 SQL。
- **M2 → M5（D03/D04/D10）**：会话目录、消息分页、微信账号视角选择命令、增量变更通知；UI 手动选择不等于采集切换。
- **M6 → M2（D11）**：迁移注册、鉴权主体、事务/错误模型、隐私审计基线。

## 8. 实施拆分与可并行任务

1. **M2-A（优先）**：领域模型、DTO、兼容现有 POC Schema 的迁移方案、唯一性/索引草案；提供 Mock `IMessageQuery`。
2. **M2-B**：实时统一入库和可重复投递的幂等写入；与 M1 使用真实 DTO fixture 联测。
3. **M2-C**：历史合并、来源证据、冲突待核对与批次计数；可以与 M2-D 并行，只由一人负责 M2 数据迁移冲突合并。
4. **M2-D**：视角作用域、幂等设置、版本冲突和方向投影；M5 同期可使用 Mock。
5. **M2-E**：分页/游标、历史检索与 Web 事件（保存后通知）。

## 9. 验收场景

| 编号 | 场景 | 通过标准 |
|---|---|---|
| M2-T01 | 同一 `wx4:` 消息连续上报 3 次 | 只有 1 条逻辑 Message，来源关联无重复 |
| M2-T02 | 实时与历史提供相同可验证源键 | 同一个 `messageId`，可查两个来源 |
| M2-T03 | 正文相同、来源消息键不同 | 保留两条记录 |
| M2-T04 | 模拟不同 shard 的派生 ID 冲突 | 不静默合并，`NeedsReview` 可查询 |
| M2-T05 | 会话 A 请求会话 B 消息/校准 | 授权范围拒绝，返回值不泄露 B 数据 |
| M2-T06 | 两个可比较数字 sender 初始化 | 小 ID 记为本人默认，方向带 `DefaultSmallSenderId`/待确认状态 |
| M2-T07 | 待确认期间触发 AI/画像流程 | 不创建或更新参与者画像；临时方向不作为风格证据 |
| M2-T08 | 用户点“互换身份”但未确认 | 仅有 UI/查询预览变化；映射、方向投影、画像及记忆沉淀均不变 |
| M2-T09 | 用户点“确认身份”提交当前候选 | 映射持久；方向投影更新；源 sender 不变；发布确认事件供 M3 首次沉淀 |
| M2-T09a | 已确认后互换预览但未确认 | 当前视角及既有派生画像不变 |
| M2-T09b | 已确认后确认新视角 | 新版本方向重投影；旧派生内容失效并按新视角重建；人工记忆保留 |
| M2-T10 | 相同视角设置请求的 HTTP 重试 | 同幂等键返回同一结果；相同选择幂等；旧 version 冲突返回 409 |
| M2-T11 | 没有恰好两个 sender / namespace 混杂 | 默认不可生成，保留 Unknown，画像暂停 |
| M2-T12 | 批次部分错误、页面断连 | 已入库记录不丢，批次状态清晰，游标可补拉 |

## 10. 评审结论与待决项

**批准开发的前置：** D01/D02/D04 与 M1/M5 冻结，`wx4:` 的精确字节构造有 Golden fixtures，数据库迁移不破坏当前 POC。**不阻塞的后续项：** 多分片键的新方案、群聊、媒体支持、超出“小 ID 默认 + 用户确认/互换”的自动方向判定。

需联合裁决：现有 POC 表上采用新增来源表还是逐步迁移；`identityNamespace` 的稳定计算方法；分页时间/游标的既有兼容策略。此三项是工程设计选择，不得被写作已验证事实。
