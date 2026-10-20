# M1 微信接入与消息采集：详细设计

> 2026-10-20 实施更新：此处原有的小 ID / 人工校准方向方案已被《WeChat_Automatic_Sender_Identity_Implementation_Spec.md》取代。实时和历史采集须共用 wxid 归一化，并按来源分片保存身份。

> 状态：待评审；参照总体架构 v1.0-Draft / Contracts v1。  
> 开发定位：独立 Windows 本机只读进程 + ASP.NET Core 接入薄层；M1 可以在 M2 使用 Mock 吞吐时并行开发。

## 1. 目标与验证基线

M1 把 Windows 微信本机**可读取**的原始消息事实传给 M2，而不关心联系人记忆、Agent 或前端 UI。首版仅承诺一个已授权普通一对一实时采集目标及指定会话的有界文本历史读取。实时双向采集/方向验证在 Windows 微信 4.1.12.26 完成；历史 POC 单独在 4.1.15.13 完成，使用 `wechatauto-replica==1.2.5.1` 查询单分片 92 条文本并与 5 条实时源键对账。这不是同一版本端到端通过或全版本/跨 shard 支持证明。

优先复用当前 `scripts/local-wechat-reader.py`、`ReceiveOnlyWeChatDB` 与已通过的历史 POC 读取路径；**不要导入整个旧 WPF 导出软件**，也不要把 HTML/TXT 当机器导入协议。

## 2. 物理边界与目录建议

```text
Windows 微信客户端（必需、按兼容版本运行）
  ↓ 本机授权只读 DB 读取
M1 Windows Reader (Python/wechatauto-replica)
  ├─ 实时：watch one authorized account/conversation
  ├─ 历史：bounded text range read (same DB boundary)
  ├─ raw source identity preservation
  └─ authenticated ingress/status channel
       ↓ D01 RawMessageEvent / D02 HistoryBatch
M1 ASP.NET Core Adapter (thin, authenticated)
       ↓ internal IMessageIngestion
M2 Message Domain
```

本机与服务端可部署同机或分机，但不能假设远端服务器能访问 Windows 本地数据库。读取器不接收 Agent 发来的任意本地文件路径、SQL 或 Shell 命令。读取器遵从最小访问权限与只读使用边界；版本变动时必须可以显式报错，而非无声输出空结果。

## 3. M1 内部组件

| 部件 | 职责 | 不做 |
|---|---|---|
| `IWeChatReadSource` | 封装现有读取包、会话发现和源字段读取 | 不持久化业务消息 |
| `RealtimeCaptureWorker` | 跟踪 ActiveCaptureTarget、新消息及读取断点/状态 | 不决定 Web SelectedConversation |
| `HistoryReadWorker` | 按指定账号、会话、类型、时间/上限遍历**实际发现**的分片 | 不根据正文推测去重 |
| `SourceEventAdapter` | 输出 Contracts v1 `RawMessageEventV1`，保留所有可读源键 | 不将 `server_id` 等同于实时 `wx4:` |
| `ReaderTransport` | 将受限事件提交 M2 接入薄层，处理响应与受控重试 | 不自行把发送成功等同消息落库 |
| `CaptureStatusReporter` | 当前账号/会话、读取器活动、最近成功提交、缺口警示、兼容性错误 | 不承诺“全部聊天已完整同步” |
| `HistoryJobCoordinator` | 接受经后端授权的历史任务、报告分片与批次进度 | 不变成任意远程命令执行器 |

建议保留现有 Python 实现直至基本闭环稳定；跨语言传输经 JSON 契约即可。不要为统一语言而无必要地把读取器改写为 .NET。

## 4. 实时采集规范（D01）

1. 后端显式配置**单一** `ActiveCaptureTarget(accountExternalId, conversationExternalId, Direct)`；与 UI 当前选中会话分别存储。
2. 本机读取器核实实际活动数据库账号与授权目标相符；无权限、账号变化、版本/库结构未知则停止该目标并报告状态。
3. 获取文本原始字段，尽可能提供 `localId`、`sortSeq`、`serverId`、`shardFingerprint`、`messageTable`、`rawSenderId`、`sourceOccurredAt`。如公共字典丢失 `server_id`，在**现有授权的只读 DB 连接边界**提取，而非新建另一路解密。
4. 生成 `RawMessageEventV1`，附 `sourceKind=Realtime`；历史 POC 验证的 `wx4:` 计算规范要与 M2 共用 Golden fixtures；M1 可以提供已经计算的 `externalMessageId` 作为来源属性，但由 M2 最终校验。
5. 上报给后端并等待持久化回执；网络断开可以短期重试同一事件（幂等依赖 M2），不能默默标记为已处理。
6. 若期间有不确定的采集空窗：记录 `CaptureGapPossible`，提示用户可手动历史补录，但不声称自动恢复全部消息。

**错误层次：** 读取器进程在线 ≠ 微信源 DB 可访问 ≠ 当前会话被监听 ≠ M2 已保存消息。状态指标须分开。

## 5. 历史补录读取（D02）

### 5.1 输入限制

`HistoryReadRequestV1`：`jobId`、已绑定账号/会话、`fromUtc?`、`toUtc?`、`maxRows`、`messageType=Text`、`requestedBy`/授权上下文。首版只读普通私聊；时间范围、单批上限和取消策略由服务端硬限制，不能由浏览器或 Agent 指定任意 SQLite 路径。

### 5.2 读取算法

```text
授权并校验目标账号/会话
→ 使用当前读取包的会话/消息表发现逻辑列出全部命中分片
→ 逐分片进行有界文本查询，保留 shard 指纹与原始源键
→ 采用稳定排序、分片内/跨分片去重检测，明确数量上限及截断
→ 分小批发送 HistoryBatchV1（含 jobId/isLastBatch）
→ M2 返回 Matched / Created / NeedsReview / Failed 计数
→ 完成时输出覆盖范围、实际扫描 shard 数、错误/截断清单
```

**特别防止旧 WPF 导出器中的两个问题：** 固定连续 `MSG0..MSG99` 且遇缺失就终止；按时间分页只返回第一个有结果的 shard。读取策略以“实际发现的分片集合”为准。跨分片稳定增量游标没有完成验证，首版可以使用**显式日期/行数有界的重复扫描 + M2 幂等**，先不承诺永久增量水位。

### 5.3 批次一致性

对无法保证完全一致的本机复制/快照，记录扫描开始/结束和可能的覆盖空窗；数据新增或源库变化时不承诺扫描即完整。任务取消需保留已被 M2 确认的已入库行、让任务进入 `Cancelled`/`Partial` 而非回滚全部。

## 6. 来源身份和发送方向

- 对应字段：当前读取包原始行具有 `local_id`、`sort_seq`、`server_id`、`real_sender_id` 等候选；`server_id` 在本次 92 条中非空且互异，但未证明全域唯一。
- 为 M2 提供 `shard`、分片指纹、消息表、原始 `real_sender_id` 与可信 `sender_wxid`。分片内 `Name2Id` 是短 ID 的唯一有效解析范围；严禁按较小 ID、昵称或会话气泡推断方向。
- 当前账号 wxid 由活动账号目录的 `WeChatDB.get_self_info().username` 读取，并与 `WeChatDB.wxid` 核对；读取失败时 Reader 显式报账号身份不可用，不能继续猜测。
- M1 的实时监听与历史补录共用同一个消息归一化方法。通过 sender wxid 与当前账号 wxid 自动写入方向；无法可信解析时写入 `Unknown`，保留原始身份字段。
- 不同 shard 存在相同 `local_id/sort_seq` 时必须保留 shard 来源，不能基于同一旧派生键就无声合并。

## 7. 控制和状态接口

| 端口 | 输入/输出 | 约束 |
|---|---|---|
| `IReaderControl.SetCaptureTarget` | 一个授权目标 → 生效状态 | 不等同于 M5 当前会话选择 |
| `IReaderStatus.GetStatus` | `ReaderStatusV1` | 返回 ReaderConnected、CaptureActive、CaptureGapPossible 等分离状态 |
| `IHistoryRead.Start` | 有界 `HistoryReadRequestV1` → jobId | 由后端授权、与 M2 记录的任务关联 |
| `IHistoryRead.Cancel` | jobId → 状态 | 幂等取消，不能删除已入库历史 |
| `IMessageIngestion.IngestAsync` | D01 → Receipt | M1 重试相同事件不产生业务重复 |
| `IMessageIngestion.IngestHistoryBatchAsync` | D02 → BatchReceipt | 按批计数、含部分失败 |

建议状态机：`Stopped → Connecting → Ready → Capturing → Degraded → Recovering → Capturing`；异常兼容性失败可以进入 `Incompatible`，并提供人工干预。状态和页面在线状态相互独立。

## 8. 安全与失败处理

- 本地凭据/明文数据库（如读取包需要）只在本机必要位置短时使用；系统不把这些材料传给 Agent 或浏览器。
- 只读 DB 访问、最小文件系统权限；临时文件关闭与清理可追踪；任何日志/错误不得出现正文、源 ID、解密材料。
- 网络连接认证沿用 M6 提供的 reader-specific credential，不能直接复用 Web 用户 cookie；Web 提交的目标需要服务端再次验证。
- 任何失败不触发微信消息发送、自动点击、绕过微信客户端版本限制等后门操作。

## 9. 开发子任务与验收

| 子任务 | 可否在 M2 未完成时推进 | 验收 |
|---|---|---|
| M1-A：包装现有读取器并增加源字段透传 | 是，以 Contracts v1 + Mock ingest | 实时事件保留原始 ID；不改变现有采集路径 |
| M1-B：授权目标和 ReaderStatus | 是，依赖 M6 最小认证 Mock | 错账号/错会话不采集，状态可解释 |
| M1-C：指定会话有界文本历史读取 | 是，使用历史 POC fixture | 真实旧文本能读取，遍历已发现 shard，截断显式 |
| M1-D：批次提交/取消/进度 | 部分，依赖 M2 BatchReceipt | 重试可恢复，失败不是“成功同步” |
| M1-E：与 M2 联合回归 | 否，等 M2-ingest 就绪 | 5/5 canary 键不变；同一旧消息多次送入不重复 |

必测用例：错误微信版本返回明确错误、活动账号切换时停止采集、网络失败重试、读取分片缺号、一个会话跨两个分片、源字段缺失、历史范围超限、取消中途、仅读文本/不发消息。

## 10. 评审清单及未决选择

在任何真实数据试跑前，必须评审 Windows 只读来源的合法授权、临时明文文件生命周期、账号和会话授权、已有 POC 不回归，以及导入接口只增不删。未决：sender index 稳定作用域和 shard fingerprint 算法、跨分片真实兼容性、源库复制瞬间的一致性。它们进入后续兼容性/增强测试，不应在没有样本时写成已证明。
