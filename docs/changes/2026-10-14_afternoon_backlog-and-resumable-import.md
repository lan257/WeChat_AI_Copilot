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
- 极简模式视觉还原、`execCommand` 二级复制兜底、手机 HTTPS 配对仍未在真实设备验收。

---

# 续：`#15` 发行门槛（可交付的包与安装步骤）

评审 §4 说“当前是源代码 + 启动脚本，不是开箱即用的可分发 Windows 应用”。这一批把它推到
“包 + 校验 + 安装 + 升级/回滚有文档”，仍然**没有**代码签名。

- 新增 `scripts/Build-ReleasePackage.ps1`：自包含生成 → 打包（保留仓库布局，启动脚本零改动复用）→
  写 `release-manifest.json` / `SHA256SUMS.txt` / `VERSION.txt` → zip → 对 zip 反开自检必需文件与入口页
  是否指向 `/v8/main.js`。`-SkipPublish` 会先比较源码与已生成应用的修改时间，源码更新过就拒绝，
  避免把昨天的程序发出去（这条在实际开发中真的踩到了：第一次 `-SkipPublish` 打出了昨天的 wwwroot，
  发布包里既没有新的 `no-store` 也没有新的补录按钮）。
- 新增 `scripts/Install-FromPackage.ps1`：核对 zip SHA-256（缺 `.sha256` 或值不符直接拒绝）→ 解压到临时目录
  并按清单逐文件核对哈希 → 检查目标目录没有正在运行的本程序 → 镜像安装到
  `%LOCALAPPDATA%\Programs\WeChatAiCopilot`。组件准备与快捷方式继续走
  `Setup-TrialMachine.ps1`（`-RunSetup` 串起来），不留第二套安装逻辑。
- 两个脚本自身踩到的 PowerShell 5.1 坑：`-LiteralPath` 不吃通配符（导致复制出 5 MB 的残包）、
  `Get-Content -Raw` 默认按 ANSI 解码无 BOM 的 UTF-8（导致中文文件名核对失败）、
  .NET Framework 的 zip 条目名用反斜杠（导致自检误报缺文件）。
- `docs/operations/Release_and_Upgrade.md`：打包、交付三件套、安装、升级（升级前自动备份 + 恢复演练）、
  回滚，以及**未签名**的实话（SmartScreen 提示怎么处理，绝不建议关闭安全功能）。
- `Trial_Deployment_Guide.md` / `Agent_Install_Runbook.md` / `Local_User_Guide.md`：改成包优先，
  git 源码路径降为开发者备选；顺手纠正“首次只导入最近 90 天”“建议要等 30–60 秒”两条已经过期的说法。
- `.github/workflows/ci.yml`：Windows job 不再自己 `dotnet publish`，改为调用同一个打包脚本，
  然后像收件人一样安装、在没有数据库的情况下启动安装后的 exe、验证 `/health/live` 与
  `/v8/main.js` 的 `Cache-Control: no-store`，并把 `artifacts/release/*` 作为构建产物上传。

### 本机实测

| 步骤 | 结果 |
| --- | --- |
| 打包 | 631 个文件、52.4 MB、zip 自检通过；`RELEASE.txt` 与 `Install-FromPackage.ps1` 落在包外 |
| 安装（模拟收件人目录） | SHA-256 一致 → 631 个文件逐一哈希核对通过 → 安装到位 |
| 安装后启动 | 无数据库配置下 `/health/live` 200、根路径 200、`/health/ready` 如实返回 `database: not-configured`；`/`、`/v8/main.js`、`/v8/styles.css`、`/v8/drawers.js` 全部 `Cache-Control: no-store`；包内 `drawers.js` 含新的 `backfill-full` |
| 缺 `.sha256` | 拒绝安装并提示 |
| `.sha256` 值不符 | 拒绝安装，回报期望值与实际值 |
| `-SkipPublish` 遇到旧产物 | 拒绝并指出源码更新过 |
| 回归 | .NET 266 项、reader 31 项全部通过；两个脚本通过 PowerShell 解析器检查 |

**未验收**：真机（另一台电脑）上没有干净环境可测，CI 的 Windows runner 流程是本机按同一路径模拟的；
桌面快捷方式、微信登录状态与只读组件在别人机器上的表现仍未验证；zip 本身不是字节级可复现产物。

---

# 续：AI 队列指标 + 一次真实的故障排查（含两条更正）

## 队列公平性与指标

两个工作循环 + 全局 FIFO 意味着一个联系人连发多条任务时，其它联系人只能干等，而且界面只有一个转圈。

- `AsyncAgentTasksV6.ExecuteNextAsync` 领取任务改为先按“该会话已有多少待处理/进行中”排序，同会话内仍按创建时间；
  新用例 `ExecuteNextClaimsWithAFairerConversationAheadOfAnOlderBusyOne` 证明 A 有两条、B 有一条时先跑 B，
  三条任务最终都完成。
- `/api/operations/status` 增加 `agentQueue`（待处理、进行中、最长等待秒数、最忙会话任务数），
  运行状态的「AI 任务执行」卡片直接显示。真实环境实测：4 条任务分属 4 个会话 →
  `{queued:2, running:2, oldestQueuedSeconds:2, busiestConversationTasks:1}`，卡片显示
  “队列 2 待处理 · 2 进行中 · 最长等待 29 秒”；排空后回到 `queued:0`。

## 更正一：不是并发引起的失败

我先前在对话里说“并发 4 个任务全挂，说明并发有缺陷”。**这个结论是错的**：随后单个任务同样在约 80 秒失败，
本机 `network.json` 指向的代理端口 7890 当时没有在监听，直连也超时。故障是模型出口不通，与并发无关。
上面的公平性改动本身仍然有效（有用例），但它既不是这次故障的原因，也不是它的修复。

## 更正二：真正值得修的是“失败没有原因”

真实失败在界面上只剩 `CODEX_CLI_EXIT_NONZERO`，谁也不知道该做什么。查下来有两处缺陷：

1. `SystemCodexCliProcessRunnerV1` 只要 stderr 超过 8 KB 就**放弃分类**直接返回 Unknown，
   而 Codex 的重试噪声很容易超过 8 KB。现在保留有界头部 4 KB + 有界滚动尾部 4 KB，两端都参与识别；
   原始文本仍然只在固定缓冲里，用完 `ZeroMemory`，不返回、不写日志、不落库（用例继续断言
   `SYNTHETIC_SECRET_MARKER` 不会出现在结果里）。
2. `CodexCliStderrClassifierV1` 的网络短语表里没有真实错误的写法。用一个**不含任何聊天内容**的自定义
   提示复现同样的失败后统计，实际文本是 `error sending request for url (...)` 与
   `failed to refresh available models: request timed out`，而表里只有 `connection refused`、`etimedout` 等；
   补入这两条精确短语（不加宽泛的 `timed out`，避免误判）。

另加两处“让人自己发现”的入口：

- 启动器注入代理后立刻探测该端口是否可连（700ms），连不上就在启动窗口写明“AI 任务大概率会以
  `CODEX_CLI_NETWORK_FAILED` 失败”，并给出两条出路（开代理 / 删 network.json 直连）。
- `wwwroot/v8/api.js` 补 `CODEX_CLI_NETWORK_FAILED` / `CODEX_CLI_AUTH_REQUIRED` / `RUNTIME_TIMEOUT` 的中文说明。

### 真实端到端验证

同一条失败路径，改动前后的错误码：

| 时机 | 结果 |
| --- | --- |
| 改动前（代理端口未监听） | `Failed / CODEX_CLI_EXIT_NONZERO / 80.3s`，界面只说“请求失败” |
| 改动后（同样条件下重跑一次 Quick） | `Failed / CODEX_CLI_NETWORK_FAILED / 80.4s`，界面显示“AI 连不上模型服务：本机代理或网络出口没有就绪……” |
| 启动窗口 | 出现“警告：本机代理 127.0.0.1:7890 现在连不上……” |

回归：.NET **271 项**通过（PlatformTests 46→50，含头/尾识别、长输出不误判、两条新短语），reader 31 项通过。
代理恢复后的成功路径沿用今天早些时候的实测（Quick 19.4s、Balanced 28.3s），本轮没有再次产生成功的模型调用，
因此“`NETWORK_FAILED` 之后恢复出口即正常”这一步属于**推断，未实测**。


