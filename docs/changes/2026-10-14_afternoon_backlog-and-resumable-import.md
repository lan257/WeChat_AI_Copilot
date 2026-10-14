# 变更说明 · 2026-10-14 下午（评审缺陷批 + 可续扫全量导入）

分支 `feat/v8-product-implementation`。承接 `临时文件/WeChat_AI_Copilot_V8_Code_Optimization_Review_2026-10-14.md`：
使用者指示“§3 画像三层暂缓，其他按缺陷修，完成之前非必要不暂停”。本批共 6 个提交（`39e6cfe…1589988`），
上半段（Quick 不复核、运行状态如实显示、静态资源缓存）见 `docs/changes/2026-10-14_quick-no-review-and-status-truth.md`。

测试基线：本批结束时 **.NET 266 项通过 / 0 失败 / 0 跳过**，**reader 离线 31 项通过**。
正式库由启动器在升级前自动完整备份并做恢复演练，随后依次应用 `022`、`023`。

---

## `39e6cfe` · 画像三层模型记为已决策暂缓

评审 §3 建议把人物数据拆成“我的账号 / 联系人本体 / 关系会话”三层。使用者判断当前设计不成熟，明确要求先不做。
新增 `docs/decisions/2026-10-14_three-layer-portrait_deferred.md`，写清当前事实基线：
`account_expression(account_id)`（019 迁移第 1 行）、`contact_portraits` 唯一键是
`UNIQUE(account_id,conversation_id,revision)`（019 第 9、20 行，即只有会话一层，同一联系人两个会话各存一份画像）、
`memory_items.subject_type IN ('WechatAccount','Contact')`（002 第 4 行），以及暂缓期间的硬约束：
不占用 `023` 及以后的迁移号、不改 `PortraitServiceV6` 的归属键、重新启用需要先出可评审设计。

顺带纠正两条已经过期的状态记录：分段耗时统计其实已经实现，“极简强制 Quick”也不成立。

---

## `fea6f44` · 记忆沉淀未启用时不再每 5 分钟刷告警

`Memory:ConsolidationRuntime` 启动器默认不设，本机 5 条 `conversation.perspective-changed` 事件因此判定为
`generator_unavailable` 并按指数退避封顶 5 分钟无限重试：每轮都要占用事件、调用处理器、写一次 warn 日志。
这不是会自愈的瞬时故障，而是“能力没配”。

- `Platform/Outbox/OutboxDeliveryContractsV1.cs`：新增 `CapabilityRetryDelay`，默认 30 分钟，并校验它不小于退避上限。
- `Platform/Outbox/ConversationChangeOutboxDispatcherV1.cs`：`generator_unavailable` /
  `generator_unavailable_reprocess_queued` 走长等待并降为信息级日志；`generator_failed` 这类真实故障仍走原退避与 warn。
- 事件不丢：仍在队列里，配置好沉淀运行时后会被正常处理。
- 4 条新用例（含“退避上限档位下仍走短退避”的反证）。

---

## `63f2a4c` · 历史补录 60 分钟执行硬时限

每个被接受的批次都会把 `updated_at` 刷新，读取器只要还在动，租约就永不过期；而 `/next` 一次只发一个任务，
一个不死的老任务会把后面所有联系人的补录一直堵着。现在在租约过期之外再加总时限：
`started_at` 超过 60 分钟即 `Failed / READER_JOB_DEADLINE_EXCEEDED`（全量任务在 `1589988` 里放宽到 8 小时）。
读取器收到 409 会中止本次扫描，不会继续白读本地库。

---

## `4f77c7c` · 预生成调度游标落库（迁移 022）

`ConversationLifecycleV6` 用内存字典记“上次看到的 `message_set_revision`”。重启后每个会话都是第一次见到，
被当成没有变化，于是**停机期间到达的消息永远不会再排预生成**，AutoPrep 形同失效；单会话异常还会打断整轮。

- `db/migrations/022_conversation_prep_state.sql`：`conversation_prep_state(account_id, conversation_id, last_seen_revision, needs_prep, updated_at)`，外键随会话级联。
- 首次收录只写基线，不为“一屏联系人”各排一次模型调用；修订变化才置 `needs_prep`。
- AutoPrep 关闭时清掉待办（沿用原语义，避免打开开关时突发）；取消监听的会话删除游标行。
- 单个会话调度失败只留下标记，交给下一轮，`pregen:{fingerprint}` 的幂等键保证不重复花模型调用。
- 新增 `ConversationLifecycleV6PostgresTests` 两条用例：用例里**每次轮询都新建一个实例**，
  等价于反复重启——旧实现在这组用例下会排 0 个任务，新实现正确排 1 个且不重复。

---

## `dbc808c` + `1589988` · 本机全量历史导入，可中断续扫（迁移 023，ADR-2026-10-14-02）

评审 §1.1 指出 90 天/5000 是策略而非能力上限，§2 指出扫描没有可恢复游标；使用者的原始现象是
“王继森会话有去年和上个月的记录，但补录没补上”。两件事必须一起做：只放开窗口会让“扫到一半断掉”代价更大。

设计要点（完整推导在 ADR）：

- 游标语义是“**已入库**的位置”，不是“已读到”。因此写入点放在**接受批次的事务内**
  （`PostgresMessageIngestionService`），进程被杀也不会跳过没落库的消息。
- 游标按请求范围分键（`window_key`：`full` 或 `fromTicks~toTicks`），换一个窗口不会错接旧位置。
- 全量任务用 `requested_from_utc / requested_to_utc` **同时为 NULL** 表示，不加枚举列，
  也不改“不传时间 = 最近 90 天”的旧默认，避免老界面行为突变。
- `maximum_rows` 检查约束从 `1..5000` 放宽到 `1..200000`（`023`），读取器自身仍保留行守卫。
- `/next` 返回 `resumeCursors`，`reader.py` 按微信分片表从 `last_local_id` 之后继续；畸形游标条目直接忽略，
  绝不拿负值当起点。
- `ReadJobAsync` 原本对空范围会抛异常，已改为空值安全，并新增 `fullLocalHistory`、`resumedScannedRows`。

真实数据验收（本机正式库，已授权会话）：

| 会话 | 结果 |
| --- | --- |
| 爸爸 | 全量任务 `Completed / read=109 / matched=109 / created=0 / failed=0`，游标累计 109；**紧接着第二个全量任务 `read=0`**，即从已入库位置继续而非重扫 |
| 王继森 | 全量任务 `read=442 / matched=184 / created=257 / needsReview=1 / failed=0`，`messageSetRevision` 368 → 809；翻页统计该会话现有 447 条可读消息，跨度 **2025-11-03 → 2026-10-14** |
| 界面 | 正式 V8 补录页在真实数据下渲染“续扫：本机该范围累计已扫描 109 条，含此前中断的扫描”和“范围：本机全部历史” |

界面差异按门禁同时出了 **V8.2 候选**（基线 SHA `f57fe56b…`，候选 SHA `556e9fd5…`，只在「历史补录」页加两张范围卡、
一个全量按钮、续扫计数与完成文案），并用真实点击在浏览器跑完“设置 → 联系人与会话 → 历史补录 → 导入本机全部历史”。
候选文档里写明：若这项界面差异不获批准，只需回退 `wwwroot/v8/drawers.js` 的对应两处，后端能力与迁移可原样保留。

诚实标注的未覆盖项：中途真的杀掉读取器这条路径没有实测；历史覆盖仍显示 `Unknown`（`coverage` 是既有硬编码占位）；
没有为 `fullLocalHistory` 单独写越权用例。

---

## 已知未做

- V8.1「采集异常」候选仍待批准（本批没有擅自实现）。
- §4 发行门槛（可复现便携包、安装器、签名）仍在进行。
- 极简模式视觉还原、`execCommand` 二级复制兜底、手机 HTTPS 配对仍未在真实设备验收。
