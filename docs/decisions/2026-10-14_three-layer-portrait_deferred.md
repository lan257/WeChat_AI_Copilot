# 决策记录 · 人物画像三层模型暂缓（DEC-2026-10-14-01）

- 状态：**已决策：暂缓，不实现**
- 决策人：使用者（2026-10-14 会话内明确指示“§3 画像三层模型：先别做了，这个我觉得现在的设计还不行”）
- 影响范围：`docs/design` 的画像方向、`copilot.contact_portraits` / `copilot.account_expression` / `copilot.memory_items`、M3 上下文与 M4 Agent 读取画像的方式。
- 相关评审：`临时文件/WeChat_AI_Copilot_V8_Code_Optimization_Review_2026-10-14.md` §1.2、§3。

## 1. 暂缓的是什么

评审建议把人物数据拆成三层持久化对象：

| 层 | 建议对象 | 归属键 |
| --- | --- | --- |
| A | `AccountSelfProfile`（我本人的表达与边界） | `account_id` |
| B | `ContactProfile`（联系人本体长期画像） | 联系人身份，跨会话 |
| C | `RelationshipProfile`（这段关系/会话的相处方式） | `account_id + conversation_id` |

## 2. 当前真实状态（不改代码，只作事实基线）

| 事实 | 证据 |
| --- | --- |
| “我本人”的表达已按账号存，但与画像分开 | `db/migrations/019_portraits_expression.sql:1` `account_expression(account_id PRIMARY KEY)` |
| 画像只有“会话”一层，没有联系人本体层 | `db/migrations/019_portraits_expression.sql:9` `contact_portraits`，唯一键 `UNIQUE(account_id,conversation_id,revision)` |
| 记忆条目已有主体类型，但没有分成三层对象 | `db/migrations/002_memory_items.sql:4` `subject_type IN ('WechatAccount','Contact')` |
| 生成画像仍以 `account_id + conversation_id` 为准 | `PortraitServiceV6.GetAsync` 先由会话取 `contact_id` 读记忆，最终读写仍按会话键 |

即：A 层近似存在但形态不同，B 层缺失，C 层就是现状。**同一联系人的两个会话目前会各存一份互不相干的画像。**

## 3. 为什么暂缓

使用者判断“现在的设计还不行”。按项目约定，与最新原型不一致的产品设计必须先出方案并经审核，才允许动正式代码与迁移；
在没有重新批准的设计之前实现三层模型，等于用一次不可逆的数据库迁移去固化一个被否决的形态。

## 4. 暂缓期间的硬约束

1. 不新增画像相关表、列或迁移；当前最新迁移是 `021_conversation_view_state.sql`，下一个空号 `022` 不得用于画像三层。
2. 不改 `PortraitServiceV6` 的归属键，不改 M3/M4 读取画像的方式。
3. 不因“缺少联系人本体画像”而在 AI 上下文里补猜测性描述；上下文继续只用已确认的证据。
4. 任何想推进这一层的改动，先补一份可评审的设计（归属、跨会话合并、证据可追溯、迁移与回滚），
   走 `docs/product/candidates` 或本目录的新决策记录，经使用者批准后才写代码。

## 5. 重新启用条件

- 使用者重新给出画像分层的产品意图，或
- 出现可验证的缺陷需要 B 层才能解决（例如同一联系人多会话画像互相矛盾），此时先提方案再实现。
