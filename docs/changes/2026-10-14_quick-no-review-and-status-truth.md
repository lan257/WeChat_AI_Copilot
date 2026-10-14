# 变更说明 · 2026-10-14（快速档不再复核、历史导入结果可见、运行状态如实显示）

分支 `feat/v8-product-implementation`。响应 `临时文件/WeChat_AI_Copilot_V8_Code_Optimization_Review_2026-10-14.md`
中标为缺陷的条目；画像三层模型（§3）按使用者要求暂缓，未改动任何数据结构或迁移。

---

## `417feb4` · Quick 档不再执行复核

### 为什么要改

Quick 的界面文案写着“优先速度”，但运行时只按“是否 Max / 是否启用复核 Skill”决定是否复核。
这台电脑的真实会话启用了 2 个复核 Skill，于是 Quick 也要跑第二次模型调用，实测复核固定吃掉约 12 秒，
“优先速度”变成一句空话，任务详情里还多出使用者看不懂的 `reviewMilliseconds`。

### 改了什么

- `Modules/Agent/Runtime/CodexCliAgentRuntime.cs`：复核条件改为
  `档位 != Quick && (档位 == Max || 启用的复核规则数 > 0)`。Max 仍强制独立复核，Balanced/Deep 仍受复核 Skill 控制，
  只有 Quick 明确不花第二次模型调用。复核提示词不再自称“Max 档”，改为“独立复核”，因为它同样服务 Balanced/Deep。
- `wwwroot/v8/chat.js`：建议栏的“N 项复核 Skill”按深度计算，Quick 显示 0，避免声明并不会发生的开销。

### 验证

- 全量测试：MemoryTests 64、DomainTests 50、PlatformTests 42、AgentTests 102，共 **258 通过 / 0 失败 / 0 跳过**
  （两个 PostgreSQL 门控用例改由 Balanced/Max 覆盖复核链路，避免门槛把复核变成无证据代码；
  用例夹具补上读取能力与上下文工具会话，使非 Quick 档在合成库里能真正跑通检索路径）。
- 真实微信会话（同一联系人、同一人设）实测：
  - Quick：Completed，总计 **19.4s**，只有 `cliMilliseconds=19290`，**没有** `reviewMilliseconds`；
  - Balanced：Completed，总计 **28.3s**，`cliMilliseconds=16116` + `reviewMilliseconds=12163`，阶段出现 `Reviewing`。
  即复核门槛按预期只在非 Quick 档生效。
- 界面文案在真实页面渲染确认：Quick 显示“0 项复核 Skill”，Balanced 与 Max 显示“2 项复核 Skill”。

---

## `2862a39` · 运行状态显示首次历史导入的真实结果

### 为什么要改

把首次补录改成非阻塞之后，失败只写一个 `initial-history-warning.json`，界面照常显示一切正常，
使用者唯一能察觉的现象是“消息怎么这么少”，没有任何原因和重试入口。

### 改了什么

- `Modules/Operations/OperationsEndpointsV1.cs`：`GET /api/operations/status` 增加 `historyImport`，
  优先返回告警文件的 `Failed` + 错误码 + 原因，其次按 `initial-history-imported-*.json` 判定 `Completed`，
  都没有则 `NotRecorded`；读取失败回 `Unknown`。只读状态文件名与固定文案，不含聊天正文、账号或凭据。
- `wwwroot/v8/drawers.js`：运行状态新增“本机历史导入”卡片，失败时带错误码并指向「联系人与会话 → 历史补录」重试。

### 验证

- 真实环境返回 `{"state":"Completed","message":"本机历史导入已完成。"}`，卡片渲染为“已完成”。
- 失败分支只覆盖到代码路径与文案，未在这台已成功的机器上制造真实失败（不删除正式导入标记），
  因此 **Failed 显示态未验收**。

---

## `55059a5` · 运行状态改读真实字段，补齐中文状态标签

`health()` 读的是 `/api/operations/status` 里根本不存在的 `memoryState`，所以“长期记忆沉淀”无论运行时是否启用
都固定显示“需身份确认”。改为读 `memoryConsolidationRuntime`，本机实测显示“未启用”。
同时给 `dom.js` 的 `statusLabel` 补 `NotEnabled → 尚未启用`、`Configured → 已配置`；
之前手机安全访问卡片把枚举原值 `NotEnabled` 直接印在页面上。

---

## 文档纠正（本次提交）

- `Local_User_Guide.md`：删掉“只要启用复核 Skill，任何深度都会多一次复核”这条已经错误的说明，
  改为按档位说明复核是否发生；补录耗时按实测改写；“自动备份与升级恢复尚未实现”改为升级前自动备份 + 恢复演练
  （`Backup-FormData.ps1 -Verify -Keep 10`，保留最近 10 份），并说明仍缺定期备份与异地副本。
- `Local_Operations_Guide.md`、`Development_Status.md`：同步上述两点，P07 档位行改为记录真实分段耗时与实测数字。

---

## `53d67fd` · 静态资源不再被浏览器缓存

入口页已经 `no-store`，但前端模块没有指纹后缀，浏览器仍可能复用旧的 `.js/.css`：升级后页面结构是新的、
脚本还是旧的。`Program.cs` 把 `no-store` 扩到 `.js` 与 `.css`；本机重启后实测 `/`、`/v8/chat.js`、
`/v8/styles.css` 响应头均为 `200 no-store`。

---

## 已知未覆盖

- 采集故障显示态仍是 V8.1 候选，等待批准后才接真实 `captureState=Error`。
- §2 分阶段可恢复历史补录、§5「预生成任务恢复」（`ConversationLifecycleV6` 的内存 `_revisions/_pending`
  重启即丢）需要 ADR 与迁移，本次未动。
- 历史导入的 **Failed 显示态**未验收：这台机器只有成功标记，未删除正式标记去人为制造失败。
- 手机配对、极简模式视觉还原、`execCommand` 复制第二级兜底仍未在真实设备验收。
