# 正式项目开发状态

更新日期：2026-10-10

## 当前阶段

当前按并行开发计划进入 **S3 安全回归、文档交付与基础版验收**。正式开发仓库位于 product；POC 仓库独立存放，本次修改未进入 POC。

S0 与 S1 已完成。I1–I5 已在合成数据上走过主要业务链路；真实微信账号和消息尚未用于验收。S3 的源码修复、迁移、自动回归和本机运行文档已补齐，Codex CLI 最近一次重复验收超时，真实微信试接入仍需在获准本机目标上完成。

## 里程碑

| 里程碑 | 状态 | 已验证内容 |
|---|---|---|
| S0 共享契约与工程骨架 | 完成 | D01/D02、校准与 Agent 契约、正式 API、模块测试工程和 PostgreSQL 迁移入口在本仓库。 |
| S1 六模块并行实现 | 完成 | M1–M6 主体实现；默认只读、单账号/单 Direct 会话范围和 opt-in Codex CLI。 |
| I1 Reader→PostgreSQL→Web | 合成验证通过 | Reader source identity 经 API 写入 PostgreSQL；首次为 Created、重复投递为 Matched，同一消息可由 Web 读取。真实微信未接入。 |
| I2 上下文/记忆检索 | 合成验证通过 | M2 消息查询接入 M3 上下文；人工记忆与消息证据可检索。 |
| I3 Codex CLI 建议/咨询 | 合成 API 验收通过 | 本轮在 180 秒预算下返回 3 条非空建议，运行记录为 Completed；此前两轮咨询也通过合成验收。当前本机 CLI 延迟较高，不能用 60 秒预算假定任务会完成。 |
| I4 历史/校准/画像 | 合成验证通过 | 合成历史补录、版本 3 首次沉淀、确认新视角后版本 5 重建、上下文仅加载版本 5 产物。旧版本数据仍物理留在库中，但当前读路径按版本排除。 |
| I5 UI 闭环 | 合成链路曾通过；窄屏修复已复验 | 此前已验证消息、3 条建议、复制和两轮咨询；本轮 240px 视口测得页面宽度仍为 240px，无横向溢出；API 再次生成 3 条建议。复制反馈改动的浏览器端复验尚待完成。 |
| S3 安全回归与交付 | 合成范围回归通过，真实目标验收待做 | source identity 冲突、鉴权隔离、超时进程树清理、全量模块测试与本机运行文档已完成；真实微信试接入和复制反馈浏览器复验未完成。 |

## 本轮回归证据

- .NET：212 项通过、0 失败、1 项默认跳过；跳过项为需指定 disposable PostgreSQL 的 M2 集成测试。
- PostgreSQL：已创建并清理专用 copilot_test_s3_* 临时库，source identity 集成测试 **1/1 通过**。覆盖三次重复投递、同 wx4: 键但不同 serverId 进入 NeedsReview、跨 shard 冲突进入 NeedsReview、同正文不同来源 ID 保留为两条消息、来源字段持久化。
- Reader：Python **10/10 通过**，新增精确 wx4: Golden hash 校验。
- Web：node --check src/WeChatAiCopilot.Api/wwwroot/app.js 通过。
- Platform：40/40 通过；新增 fake child timeout 测试，验证系统 runner 返回 TimedOut 并清理子进程树。
- API 鉴权：无 token 与跨 Reader/Web token 均返回 401；正确 Web token 读取人设目录返回 200；正确 Reader token 在未配置数据库的 host 上通过身份过滤后返回 DATABASE_NOT_CONFIGURED。
- Codex CLI：本轮无聊天状态的结构化 smoke 用时 117 秒；本机合成 API 建议在 180 秒预算下 Completed 并生成 3 条非空结果。60 秒的早期测试预算会得到 RUNTIME_TIMEOUT；一次大上下文试跑则因 8,000 字上限记录 AGENT_CONTEXT_BUDGET_EXCEEDED。运行审计没有记录聊天正文或提示词。

## S3 已完成的修复与交付

- M2 现在把同一来源键的两个非空且不同 serverId 视为来源冲突；新增迁移 011 把 server_id 纳入来源证据唯一键，让冲突证据可保存。不同 shard 的相同 wx4: 键继续进入 NeedsReview，不静默合并。
- Reader Golden fixture 已增加精确哈希测试。
- 极简 UI 复制成功/失败现在显示 toast；240px 状态行可收缩。咨询 Thread 的 sessionStorage 键按账号和会话组成。
- API Reader/Web token 隔离已实测；M4 测试覆盖账号/会话/Thread 越权、工具预算和不开放 Shell/Web/MCP 等限制。
- 不含聊天数据的 Codex CLI smoke 记录 117 秒；用同一合成会话进行 API 建议时将测试预算设为 180 秒并成功完成。预算应结合目标本机 CLI 延迟配置。
- 已交付[本机开发与运行手册](Local_Operations_Guide.md)和[模块接口清单](Module_Interface_Inventory.md)，涵盖启动、迁移、Reader、备份恢复和当前限制。

## 尚未关闭的基础版验收项

1. 在用户明确授权的本机账号与单个 Direct 会话上完成 Reader→PostgreSQL→Web 试接入；本轮不访问真实微信。
2. 在当前代码与目标运行环境中取得一次稳定的 Codex CLI API/UI 建议复测，并复验换批与极简复制反馈。
3. 尚无 PostgreSQL 级 M4 thread/account/conversation 越权集成回归；当前由模块测试和复合外键约束覆盖。
4. 尚未执行实际备份恢复演练；手册给出恢复到新数据库的步骤。
5. 多 shard 真实数据的 wx4: 全域唯一性尚未证明；检测到不同来源证据时当前进入 NeedsReview，不宣称跨 shard 去重已解决。
