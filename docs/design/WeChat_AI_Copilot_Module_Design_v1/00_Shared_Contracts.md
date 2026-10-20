# 跨模块共享数据契约（Contracts v1）

> 2026-10-20 实施更新：发送方向改为按消息分片 `Name2Id` 解析 `senderWxid`，再与账号 wxid 比较。本文原有的小 ID 默认和人工互换流程已被《WeChat_Automatic_Sender_Identity_Implementation_Spec.md》取代；遇到身份缺失时必须保持 `Unknown`。

> 状态：**拟冻结的首版开发契约**；供六个模块并行实现和评审，未表示现有 API 已全部存在。  
> 基线：[总体架构设计](Overall_Architecture_Reference.md) v1.0-Draft，2026-10-09。
> 冻结原则：语义先冻结，API 路径/字段名称允许经联合评审做一次协调；之后实行版本化变更。

## 1. 使用方式与强约束

- M1–M6 的实现以本文件为跨模块单一对照基线。模块文档不能单方面修改其它模块拥有的数据语义。
- 基础版：一个可访问的 Windows 微信账号；一个**获准**的一对一实时采集目标；指定会话的文本历史补录；工作区聊天、AI 侧栏、三条候选、极简小屏；所有微信回复都由用户**返回微信手动发送**。
- 两个不同状态：`ActiveCaptureTarget`（M1 采集目标）与 `SelectedConversation`（M5 当前展示目标）；切换页面不得静默修改采集目标。
- 跨账号、跨会话、跨身份命名空间的数据绝不因昵称相同或模型判断而合并。
- 历史 POC 仅证明 Windows 微信 4.1.15.13 / 单会话 / 单分片 / 文本中 5/5 `wx4:` 对账成功。**不得假设所有分片、账号、类型均已验证**。
- 不存在微信发送接口；M4 不获得微信数据库、任意 SQL、系统 Shell 或原始凭据访问权限。

## 2. 公共标识、时间及枚举

| 标识/字段 | 规则 | 所有权 |
|---|---|---|
| `accountId` | 后端内部分配的不可变 ID；外部读取器账号 ID 另存 `accountExternalId` | M2 |
| `conversationId` | 后端会话 ID；外部 `conversationExternalId` 在账号范围内映射 | M2 |
| `messageId` | 内部消息 ID；与微信源 ID 分离 | M2 |
| `sourceIdentity` | 尽可能保存原始键：`localId`、`sortSeq`、`serverId`、原始 `shard`、`shardFingerprint`、`messageTable`；消息还保存 `rawSenderId` / `senderWxid`，不可编造缺失值 | M1 生成、M2 保存 |
| `externalMessageId` | `wx4:` + SHA-256(账号 wxid/账号目录、会话、分片指纹、消息表、`localId`)；同一 `localId` 跨分片不冲突 | M1/M2 共同校验 |
| `contextRevision` | 不透明版本标记；至少随会话消息/身份解析变化更新；由服务端提供，不由浏览器构造 | M3 计算/M4 回传 |
| `batchId` / `threadId` / `runId` | M4 内部产生；必须携带不可变 `accountId + conversationId` 作用域 | M4 |
| 时间字段 | API UTC ISO 8601；`sourceOccurredAt` 可空；UI 负责当地时区展示 | 各模块 |

**枚举建议（大小写为合同值）：**

- `sourceKind`: `Realtime` / `HistoryBackfill`
- `conversationType`: 基础版 `Direct`（其它类型暂拒绝或显式不支持）
- `messageType`: 基础版 `Text`（其它类型原样标记，不假装解析）
- `direction`: `Incoming` / `Outgoing` / `Unknown`
- `directionEvidence`: 新消息使用 `Unresolved` / `SourceConfirmed`。旧库里的 `DefaultSmallSenderId` / `UserConfirmed` 仅为兼容值，不作为新方向证据。
- `perspectiveState`: 兼容字段；`Confirmed` 表示账号目录已经绑定可验证的账号 wxid，`Unresolved` 表示该账号身份未就绪，不要求会话级人工确认。
- `profileStatus`: 保留 `WaitingForConfirmation` 作为兼容状态值；它表示账号 wxid 尚不可用，而非要求用户逐会话确认。其它值包括 `FirstConsolidation` / `Accumulating` / `Consolidating` / `Rebuilding` / `Current` / `Failed`。
- `memoryOrigin`: `UserEntered` / `MessageExtracted`；人工记录立即持久化，消息抽取记忆只使用 `SourceConfirmed` 方向。
- `matchStatus`: `Matched` / `Created` / `NeedsReview`
- `historyJobStatus`: `Pending` / `Running` / `Completed` / `Partial` / `Failed` / `Cancelled`
- `agentRunStatus`: `Queued` / `Running` / `Completed` / `Failed` / `Cancelled`

## 3. D01/D02：采集 → 消息域

`RawMessageEventV1`：**传输规范示例**，不是微信数据库行或现有 API 的保证形状。

```json
{
  "schemaVersion": 1,
  "provider": "local-wechat",
  "sourceKind": "Realtime",
  "accountExternalId": "sample-account",
  "conversationExternalId": "sample-contact",
  "conversationType": "Direct",
  "messageType": "Text",
  "text": "示例文本",
  "sourceOccurredAt": "2026-10-09T02:00:00Z",
  "rawSenderId": "73",
  "identityNamespace": "message-table-and-shard-scope",
  "sourceIdentity": {
    "localId": "58",
    "sortSeq": "90001",
    "serverId": null,
    "shardFingerprint": null,
    "messageTable": "example-table"
  },
  "externalMessageId": null,
  "direction": "Unknown",
  "directionEvidence": "Unresolved"
}
```

**接口约束：** `sourceIdentity` 字段值在传输时尽量使用无损字符串；对既有 `wx4:` 算法采用与 POC 一致的原始十进制/字符串规范，不能因新 DTO 的类型转换改变哈希。M1 提供来源与观察事实；最终去重、方向、可信级别由 M2 裁定。

`HistoryBatchV1` 传输外壳：

```json
{
  "schemaVersion": 1,
  "jobId": "history-job-id",
  "accountExternalId": "sample-account",
  "conversationExternalId": "sample-contact",
  "requestedRange": {"fromUtc": null, "toUtc": "2026-10-09T02:00:00Z"},
  "items": [{"sourceKind": "HistoryBackfill", "schemaVersion": 1, "provider": "local-wechat", "accountExternalId": "sample-account", "conversationExternalId": "sample-contact", "conversationType": "Direct", "messageType": "Text", "text": "示例", "sourceOccurredAt": null, "rawSenderId": null, "identityNamespace": "shard-scope", "sourceIdentity": {"localId": "1", "sortSeq": "2", "serverId": null, "shardFingerprint": null, "messageTable": null}, "externalMessageId": null, "direction": "Unknown", "directionEvidence": "Unresolved"}],
  "isLastBatch": false
}
```

- 批次必须受限、有 `jobId`、明确作用域，能取消并报告实际覆盖、截断与失败；`isLastBatch=true` **只表示本次任务扫描完成，不表示微信历史完整**。
- 实时和历史均走 M2 同一入库/去重入口。**禁止按正文相似度去重、禁止因导入源缺失而删除旧消息**。
- 跨 shard 或来源字段缺失导致外部键不可信时，应 `NeedsReview`，不能错误使用相同 `wx4:` 直接覆盖。

## 4. D03/D04/D10：会话查询、方向解析和通知

- 会话范围仍由账号 UUID、会话 UUID 和 Web token 约束；浏览器切换会话不改变 Reader 监听目标。
- `RawMessageEventV1` 携带 `accountWxid`、`senderWxid`、`rawSenderId`，且 `sourceIdentity` 保留 `shard`、`shardFingerprint`、`messageTable` 与消息 ID。API 忽略事件传入的方向枚举并按已绑定账号 wxid 重新计算。
- 本人 wxid 与消息发送者 wxid 相等时写入 `Outgoing` / `SourceConfirmed`；两个可信 wxid 不同时写入 `Incoming` / `SourceConfirmed`；身份缺失时写入 `Unknown` / `Unresolved`。
- 账号外部 ID 已绑定到不同 wxid 时返回 `409 READER_ACCOUNT_WXID_CONFLICT`，不跨账号合并。
- 新方向解析使旧消息从 `Unknown` 变为可信方向时递增会话修订，并发出 `message.identity-resolved`；记忆侧按新持久化方向重新采集。
- 当前 Web API 没有 sender-perspective 读取或写入端点。旧 `sender_calibrations` 仅为过渡兼容存储，不驱动新消息方向。
## 5. D05/D06：消息查询、记忆与上下文

`ContextRequestV1`：

```json
{"accountId":"internal-account-id","conversationId":"internal-conversation-id","requestPurpose":"ReplyCandidates","recentMessageLimit":30,"anchorMessageId":null,"includeMemory":true}
```

`ContextBundleV1`（示意）：

```json
{
  "accountId":"internal-account-id",
  "conversationId":"internal-conversation-id",
  "contextRevision":"opaque-revision",
  "perspectiveState":"Confirmed",
  "calibrationVersion":3,
  "profileStatus":"Accumulating",
  "unprofiledTextCharacters":3278,
  "consolidationThresholdCharacters":5000,
  "consolidationSourceMessageCursor":"opaque-cursor",
  "recentMessages":[{"messageId":"id1","text":"示例","occurredAt":"2026-10-09T02:00:00Z","direction":"Unknown","directionEvidence":"Unresolved"}],
  "participantProfiles":[{"subjectType":"Contact","subjectId":"internal-contact-id","profileRevision":4,"calibrationVersion":3,"status":"Current","sourceMessageCursor":"opaque-cursor","evidenceMessageIds":["id1"]}],
  "memoryItems":[{"memoryId":"memory-1","accountId":"internal-account-id","subjectType":"Contact","subjectId":"internal-contact-id","conversationId":"internal-conversation-id","text":"人工记录的事项","origin":"UserEntered","calibrationVersion":null,"sourceMessageCursor":null,"sourceMessageIds":[]},{"memoryId":"memory-2","accountId":"internal-account-id","subjectType":"Contact","subjectId":"internal-contact-id","conversationId":"internal-conversation-id","text":"从聊天证据抽取的事项","origin":"MessageExtracted","calibrationVersion":3,"sourceMessageCursor":"opaque-cursor","sourceMessageIds":["id1"]}],
  "omissions":[],
  "generatedAt":"2026-10-09T02:00:01Z"
}
```

- M2 只提供已授权消息查询；M3 对画像版本、画像/自动长期记忆共用的未沉淀文本计数和高水位游标、上下文数量、证据裁剪及记忆优先级负责；M4 只读使用。画像主体为微信参与者（`WechatAccount` 或 `Contact`），不是当前 Web 登录用户；只有 `SourceConfirmed` 的 Direct/Text 消息可以分配到本人/联系人并进入相应画像或消息抽取记忆。方向未知时仅作中性检索。首次账号 wxid 绑定后可开始第一次沉淀；后续同一账号/会话的未沉淀文本按源版本累计，严格超过 5,000 字符时由 M3 一次处理共享窗口内全部新文本并推进共享游标。此流程由新消息事件驱动阈值检查，不使用定时任务或手动强制刷新。
- M4 工具调用使用**后端注入的账号/会话约束**，模型无法传入任意可访问其它联系人的账号 ID；返回证据 ID 而非整库内容。
- 对空结果、历史不完整、校准未知、检索截断等情况，`omissions` 应显式标注；不可在 AI 层默默补事实。

## 6. D07/D08/D09：Agent 任务和候选

`AgentTaskV1`：

```json
{
  "taskType":"ReplyCandidates",
  "accountId":"internal-account-id",
  "conversationId":"internal-conversation-id",
  "personaPresetId":"natural",
  "previousBatchId":null,
  "threadId":null,
  "userInput":null,
  "contextRevision":"opaque-revision"
}
```

`SuggestionBatchV1`：

```json
{
  "runId":"run-id",
  "taskType":"ReplyCandidates",
  "accountId":"internal-account-id",
  "conversationId":"internal-conversation-id",
  "personaPresetId":"natural",
  "contextRevision":"opaque-revision",
  "batchId":"batch-id",
  "suggestions":[{"id":"s1","text":"备选一"},{"id":"s2","text":"备选二"},{"id":"s3","text":"备选三"}],
  "status":"Completed"
}
```

`ConsultationTurnV1`：

```json
{"threadId":"thread-id","runId":"run-id","accountId":"internal-account-id","conversationId":"internal-conversation-id","userInput":"如何主动找话题？","answer":"示例分析","evidenceMessageIds":[],"status":"Completed"}
```

- `ReplyCandidates` 完成时要求**恰好 3 条非空建议**；生成失败返回错误或恢复旧批次，不用前端伪造三条“新”候选。
- `FreeConsultation` 只能在已绑定 `accountId + conversationId` 的线程继续；切换 Web 会话不能跨联系人续问。
- `previousBatchId` 用于换批次的上下文；人设 ID 在 M4 服务端映射，不接受任意 Skill 路径、Shell 命令和模型参数注入。
- `contextRevision` 变化后的旧批次仍能查看，但 Web 必须提示过时；点击复制只调用浏览器剪贴板，不代表微信已发送。

## 7. 统一错误、权限与版本协作

建议响应外壳（各模块复用，由 M6 确定最终标准）：

```json
{"error":{"code":"CONVERSATION_NOT_AUTHORIZED","message":"当前会话不可访问","retryable":false,"correlationId":"trace-id"}}
```

统一语义：`400` 格式错误，`401` 未认证，`403` 越权，`404` 资源不存在或不可见，`409` 账号 wxid 绑定冲突/状态冲突，`429` Agent 预算/限流，`503` 读取器或 Agent 不可用。错误信息不能泄漏私聊正文或隐藏账号身份。

**并行开发冻结流程**：

1. 第一次集成前由 M2 + M1 + M3 + M4 + M5 + M6 联合确认 D01、D02、D04、D06、D07、D08、D09、D10 的语义与类型；以 Mock fixture 证明生产者和消费者能交互。
2. 每次破坏性变更先建“合同变更提议”，列出新旧字段、迁移、受影响模块、Mock fixture；经拥有者与消费者双方确认后合入。
3. 模块间通过公共 DTO/接口定义包或 OpenAPI schema 共享契约；**不要让多个 Agent 各自复制一份略有差异的 DTO**。
4. 合同冻结不冻结实现技术：允许模块内部替换 ORM、查询策略、模型运行时或 UI 框架，不影响跨模块语义。
