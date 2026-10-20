# M2 账号、会话与消息管理：详细设计

> 2026-10-20 实施更新：本文原有的 sender ID 待确认和互换流程已被《WeChat_Automatic_Sender_Identity_Implementation_Spec.md》取代。方向只由账号 wxid 与分片内解析出的 sender wxid 决定。

> 详细设计状态：待独立评审；总体架构 v1.0-Draft 的具体化，不代表代码均已实现。  
> 优先级：P0／**最早实施的核心模块**；负责人需与 M1、M3、M5 签署接口契约。  
> 唯一业务责任：`ChatAccount / Contact / Conversation / Message / MessageSource`；旧 `SenderCalibration` 仅作过渡兼容数据，实时与历史消息在统一归一化后合流。

## 1. 目标和范围

本模块按账号 wxid 与来源分片内解析的 sender wxid 自动保存消息方向；指定普通一对一会话的实时采集和历史补录共用同一归一化与幂等入库路径。可创建多个会话记录，但不能把“多会话目录”误认为“多个会话同时在采集”。

明确不做：微信源数据库查询、AI 记忆抽取、Agent 执行、微信自动发送、群消息精细归属、跨 shard 未证实的自动合并。依赖 M1 提供来源原始字段，依赖 M6 的鉴权/数据库迁移基础。

## 2. 核心领域对象及建议表结构

> 以下为**逻辑数据模型建议**，实际迁移由模块所有权审核后定稿；与现有 POC `chat_messages` 迁移必须显式兼容，不要直接丢弃重建。

| 逻辑对象/表 | 最小字段与约束 | 说明 |
|---|---|---|
| `ChatAccount` / `chat_accounts` | `id`、`provider`、`external_account_id`、`display_name`、`created_at`；唯一 `(provider, external_account_id)` | 登录授权允许的账号；不凭昵称归并 |
| `Contact` / `contacts` | `id`、`account_id`、`external_contact_id`、`display_name`；唯一 `(account_id, external_contact_id)` | 显示名允许修改，不改变身份 |
| `Conversation` / `conversations` | `id`、`account_id`、`contact_id`、`external_conversation_id`、`kind=Direct`、`last_message_cursor`；唯一 `(account_id, external_conversation_id)` | 同账号中的会话唯一，属于一个账号 |
| `Message` / `chat_messages` | `id`、`account_id`、`conversation_id`、`message_type`、`text_content`、`source_occurred_at`、`ingested_at`、`direction`、`direction_evidence`、`external_message_id` | 内部 ID 永远不随来源更改；时间必须区分“发生”与“入库” |
| `MessageSource` / `message_sources` | `id`、`message_id`、`provider`、`source_kind`、`external_message_id`、`server_id?`、`local_id?`、`sort_seq?`、原始 `shard?`、`shard_fingerprint?`、`message_table?`、`raw_sender_id?`、`sender_wxid?`、`identity_namespace?`、`job_id?` | 一条 Message 可以有实时/历史多份来源证据 |
| `AccountIdentity` / `chat_accounts.account_wxid` | `account_id`、稳定账号 wxid | 绑定已验证的本机账号数据目录；不从昵称或短 ID 推断 |
| `SenderCalibration` / `sender_calibrations` | 旧校准字段 | 仅作过渡兼容数据；不参与消息方向查询或写入 |
| `ImportReview` / `import_reviews` | `id`、`job_id`、`source_ref`、`reason_code`、`status` | 来源键冲突、范围不明、字段缺失，不能静默吞掉 |

关键 DB 约束：保留 POC 已验证的 `(provider, account_id, external_message_id)` 有效来源键的唯一性；新 `MessageSource` 使用独立的来源证据索引和必要的部分唯一约束，**不能对未证实全域唯一的 `server_id` 做全局唯一**。所有 `Message`/`MessageSource` 通过 `account_id` 和所属 `conversation_id` 受控访问。要保证外键链证明会话属于账号，而不只是靠控制器输入检查。

来源键策略使用 `wx4-shard-v2`；历史 `wx4-legacy` 键仅由来源关联兼容路径识别，不把旧短 ID 方向迁移为可信结论。

## 3. 消息写入算法

```text
Ingest(sourceEvent):
  1) 鉴别 M1 来源身份、协议版本、账号与目标会话是否获准
  2) 验证字段、归一化账号/会话作用域；拒绝未授权来源
  3) 保留原始 localId/sortSeq/serverId/shard/sender 等证据
  4) 在已验证范围复算现有 wx4: 键；检查碰撞/跨 shard 风险
  5) 精确匹配：复用已有 Message，新增/关联 MessageSource
  6) 可信新键且无碰撞：创建 Message + MessageSource
  7) 来源唯一性冲突：NeedsReview；仅发送者 wxid 不可解析时继续保存 Unknown，不用正文、昵称或时间猜测
  8) 以绑定账号 wxid 和 sender wxid 计算方向；任一缺失时保留 Unknown
  9) 同一事务内提交，返回内部 messageId 和查询 cursor
 10) 提交成功后发布轻量 message.created 通知
```

**幂等与并发：** 多请求竞争以 PostgreSQL 约束 + 事务/`INSERT ... ON CONFLICT` 为最终裁定，不能只先查后插。`MessageSource` 的已存在情况不会重复创建业务消息。处理同一批次内重复来源记录也应该幂等，不能只依赖 HTTP 去重。

**时间与排序：** 事件没有合法来源时间时仍保存 `ingested_at`，UI 明示时间来源；分页排序使用稳定组合（如 `source_occurred_at NULLS LAST`、`ingested_at`、`messageId`），对 UI 输出不透明游标，前后页顺序稳定。不要把时间戳当消息唯一 ID。

**文本与非文本：** 仅 `Text` 进入首版主流程；未知类型可以记录基本类型和来源证据但不伪造正文；返回清晰“不支持显示”的状态。

## 4. 历史补录对账

- M1 决定扫描哪些本机 shard 并报告实际覆盖范围；M2 负责逐条 `Match/Create/NeedsReview`，记录每条来源证据与 job 引用；不要求 M2 访问本地 SQLite。
- 当前 `wx4:` 键按账号 wxid/账号目录、会话、分片指纹、消息表和 `local_id` 派生；同一分片/表/账号的相同来源可由实时和历史幂等命中。
- 若分片或来源身份不完整，不能把相同 `local_id` / `sort_seq` 当跨 shard 唯一；冲突转 `NeedsReview`。`server_id` 为额外证据而非全球唯一键。
- 历史行与已存在实时消息确认为同一条：新增 `MessageSource`；若源库现在补足可信 sender wxid，可把旧 Unknown 更新为 SourceConfirmed，不使用短 ID 猜测。
- **历史补录只增/关联，不基于源端缺失删除**。用户显式数据删除是另一条独立操作。
- 任务计数标准：`read`、`matched`、`created`、`needsReview`、`ignoredUnsupported`、`failed`；注意 `read` 不是完整度保证。

## 5. 发送方向自动识别

### 5.1 账号与分片绑定

- `accountExternalId` 绑定本机当前选择的数据目录；Reader 初始化时通过 `WeChatDB.get_self_info()` 获取 `AccountWxid`，并核对它与该实例的 `WeChatDB.wxid` 一致。
- `real_sender_id` 只能通过消息所在分片 `message_N.db` 的 `Name2Id.rowid -> user_name` 解析。不同分片相同的数字可代表不同发送者。
- Reader 只使用已包含分片修复的 `wechatauto-replica`，优先取经过验证的 `sender_username`，缺失时调用 `sender_username(shard, sender_id)`。

### 5.2 统一归一化与方向

实时监听和历史补录调用同一个 `NormalizeMessage(raw)`。它保留 `AccountWxid`、会话、原始分片名/指纹、消息表、`LocalId`、`SortSeq`、`RawSenderId` 和 `SenderWxid`，再以稳定 wxid 比较得出 `Outgoing`（Self）、`Incoming`（Peer）或 `Unknown`。昵称、短 ID 数值、文本气泡侧都不是方向证据。

API 再按账号绑定的 wxid 计算一次方向，不信任事件提交的方向字段。若发送者 wxid 或账号 wxid 缺失，消息照常保存但为 `Unknown` / `Unresolved`；AI 和记忆不能把它当作账号发言。

来源唯一键按账号、会话、分片指纹、消息表和 `localId` 派生。同一 `localId` 在其他分片或消息表不会覆盖已有消息；实时与历史共用同一个唯一键。

### 5.3 旧数据与兼容

`sender_calibrations` 和短 ID 方向值只作为兼容留档，不再读取或写入。升级会把这些旧推断方向改为 `Unknown`；历史消息只有在重新从微信源库读取并获得分片映射后才更新，不能单靠旧短 ID 重建。
## 6. 对外端口与接口（建议）

| 端口/接口 | 调用方 | 输入 / 输出 | 失败语义 |
|---|---|---|---|
| `IMessageIngestion.IngestAsync` | M1 | `RawMessageEventV1 → IngestReceipt` | schema 错误/账号不允许/来源冲突/后端不可用 |
| `IMessageIngestion.IngestHistoryBatchAsync` | M1 | `HistoryBatchV1 → BatchReceipt` | 不确定行独立记录；部分失败不隐瞒 |
| `IConversationQuery.ListAsync` | M5、M3 | 账号范围 → 当前授权的会话目录 | 越权拒绝 |
| `IMessageQuery.GetPageAsync` | M5、M3 | 账号+会话+游标+limit → 消息页 | 非法游标/越权/会话不存在 |
| `IMessageQuery.SearchAsync` | M3 | 账号+会话+关键词/时间范围 → 引用结果 | 查询范围截断须标记 |
| `message.created` / `message.identity-resolved` | M5、M3 | account+conversation+cursor | 不携带正文的轻量通知 |

REST 路径由共享 OpenAPI 定稿；示例：`GET /api/conversations/{id}/messages`。方向不通过 Web 校准接口写入。

## 7. 与其它模块的约定

- **M1 → M2（D01/D02）**：M1 提供原始字段、数据覆盖状态和批次边界；M2 确定归属/幂等并返回处理回执。
- **M2 → M3（D05）**：带账号/会话授权上下文的消息查询与方向证据；M3 不访问 `chat_messages` 直接 SQL。
- **M2 → M5（D03/D04/D10）**：会话目录、消息分页、已持久化方向与增量变更通知；浏览器不提供 sender 方向修改命令。
- **M6 → M2（D11）**：迁移注册、鉴权主体、事务/错误模型、隐私审计基线。

## 8. 实施拆分与可并行任务

1. **M2-A（优先）**：领域模型、DTO、兼容现有 POC Schema 的迁移方案、唯一性/索引草案；提供 Mock `IMessageQuery`。
2. **M2-B**：实时统一入库和可重复投递的幂等写入；与 M1 使用真实 DTO fixture 联测。
3. **M2-C**：历史合并、来源证据、冲突待核对与批次计数；可以与 M2-D 并行，只由一人负责 M2 数据迁移冲突合并。
4. **M2-D**：账号 wxid 绑定、分片来源唯一键、方向重算和未知身份处理；M5 同期可使用 Mock。
5. **M2-E**：分页/游标、历史检索与 Web 事件（保存后通知）。

## 9. 验收场景

| 编号 | 场景 | 通过标准 |
|---|---|---|
| M2-T01 | 同一 `wx4:` 消息连续上报 3 次 | 只有 1 条逻辑 Message，来源关联无重复 |
| M2-T02 | 实时与历史提供相同可验证源键 | 同一个 `messageId`，可查两个来源 |
| M2-T03 | 正文相同、来源消息键不同 | 保留两条记录 |
| M2-T04 | 模拟不同 shard 的派生 ID 冲突 | 不静默合并，`NeedsReview` 可查询 |
| M2-T05 | 分片内发送者映射缺行或账号 wxid 不可用 | 方向为 `Unknown`，保留 raw shard / sender ID，返回明确诊断 |
| M2-T06 | 同会话、不同 shard 重复 `localId` | 两条来源分别持久化，不覆盖 |
| M2-T07 | 同一事件分别从实时与历史入口进入 | 统一归一化、同一外部来源键幂等合并 |
| M2-T08 | 停止采集期间新增消息，恢复后补读 | 历史入口补齐数据，方向不需要 UI 确认 |
| M2-T09 | 图片、语音、文件、引用消息 | 正文不可读时仍按来源元数据解析发送者；身份缺失保持 `Unknown` |
| M2-T10 | 账号切换/重启 | 账号 wxid、Reader 目标、来源指纹和本地水位不串号 |

## 10. 评审结论与待决项

**批准开发的前置：** D01/D02 与 M1/M5 对账号 wxid、分片字段、唯一键和 Unknown 语义冻结；`wx4-shard-v2` 的精确构造有 Golden fixtures；数据库迁移保留旧来源记录。**不阻塞的后续项：** 群聊成员身份、媒体内容解码和多账号实际切换验收。

需联合裁决：现有 POC 表上采用新增来源表还是逐步迁移；`identityNamespace` 的稳定计算方法；分页时间/游标的既有兼容策略。此三项是工程设计选择，不得被写作已验证事实。
