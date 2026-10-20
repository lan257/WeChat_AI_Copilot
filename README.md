# 微信 AI 沟通助手

这是正式产品的独立开发仓库。实现按正式设计从头开发；本仓库包含产品源码、测试和正式开发文档。

## 当前实现

- **M1 Reader**：Windows 本机只读读取器，输出 D01/D02 契约 JSON Lines，可按配置提交到 Reader API。
- **M2 消息域**：消息与历史批次入库、会话内分页、发送者方向由账号绑定 wxid 与消息分片 sender wxid 自动确认；无法解析时标记 Unknown，手动短 ID 校准入口已移除。
- **M3 上下文**：已实现上下文构建、人工记忆 CRUD、证据校验，以及按已确认视角工作的 5,000 字窗口状态机、分页采集器、事件处理和 PostgreSQL 沉淀仓储；Codex 结构化生成器已实现，默认关闭，需显式设置 `Memory:ConsolidationRuntime=CodexCli`。
- **M4 Agent**：会话范围协调器、三候选与多轮咨询 API、结果持久化和不含正文的运行审计；正式启动器设置本机 Codex CLI runtime，目标机需单独安装并由使用者登录。
- **M5 工作区**：工作区双栏、极简模式、建议复制、人设切换和咨询已接入正式 API；极窄屏溢出和复制反馈缺口已修复。
- **M6 平台**：提供 Bearer 鉴权、PostgreSQL 迁移入口、存活/就绪探针，以及按会话顺序处理的 M3 outbox consumer 和租约重试。

## 开发进度

按 [并行开发计划](docs/design/WeChat_AI_Copilot_Module_Design_v1/Parallel_Development_Plan.md) 跟踪；I1–I5 是 S2 集成门，不是开发阶段。当前详细进度、验证证据和未关闭项见[开发状态](docs/operations/Development_Status.md)。

| 里程碑 | 当前状态 |
|---|---|
| S0 共享契约与工程骨架 | 已完成。 |
| S1 六模块并行实现 | 六模块主体与模块回归已完成；当前在做 S3 安全收敛。 |
| I1 Reader→PostgreSQL→Web | 合成 Reader source identity、幂等入库和 Web 读取已验证；真实当前账号完成两条身份元数据核验（Outgoing/Self、Incoming/Peer），完整浏览器流程与连续监听仍待验收。 |
| I2 上下文/记忆检索 | 合成 PostgreSQL 上下文、人工记忆和证据检索已通过。 |
| I3 Codex CLI 建议/咨询 | 合成 API 已在 180 秒预算下完成并返回 3 条非空建议；无聊天状态的本机 Codex CLI smoke 用时 117 秒。此前 60 秒测试预算不足以覆盖本机延迟。两轮咨询此前已通过合成验收。 |
| I4 历史/视角/画像链 | 合成历史补录、发送者确认、版本 5 重建和当前版本上下文过滤已通过；旧版本行仍保存在库中，但不会进入当前上下文。 |
| I5 极简 UI 闭环 | 合成消息、三条建议、复制及两轮咨询此前已通过；本轮修复后 240px 视口无横向溢出，API 已再次生成 3 条建议。复制反馈改动的浏览器端复验仍待完成。 |
| S3 安全回归与交付 | 自动化回归与两条真实身份元数据核验已完成；全新 Windows 环境完整冒烟、连续监听及复制反馈浏览器复验仍待完成。 |

## 运行 API

普通使用者请阅读[本机使用指南](docs/operations/Local_User_Guide.md)。新电脑发布包和 Agent 安装步骤见[朋友部署指南](docs/operations/Trial_Deployment_Guide.md)、[Agent 安装执行手册](docs/operations/Agent_Install_Runbook.md)与[发布升级说明](docs/operations/Release_and_Upgrade.md)。正式桌面入口为启动正式项目.cmd。

从仓库根目录运行：

```powershell
dotnet run --project src/WeChatAiCopilot.Api
```

默认不连接数据库。设置 `ConnectionStrings__FormalDatabase` 后，应用启动时会运行本仓库的版本化迁移；`GET /health/ready` 用于查看数据库是否就绪。

Web 和 Reader 使用不同的 Bearer token：`Authentication__WebBearerToken`、`Authentication__ReaderBearerToken`。正式会话范围分别由 `Web__AllowedAccountExternalId` / `Web__AllowedConversationExternalId` 和 `Reader__AllowedAccountExternalId` / `Reader__AllowedConversationExternalId` 限定。未配置数据库、token 或会话范围时，相关接口会返回明确的未配置状态。

系统只读采集微信数据；不提供自动发送或自动回复能力。独立运行 API 时 Agent 默认失败关闭；正式桌面启动器会配置 Codex CLI runtime。产品建议/咨询只有在用户通过已登录 CLI 明确发起时才发送必要会话上下文。

## 检查与测试

```powershell
dotnet build WeChatAiCopilot.sln
dotnet test WeChatAiCopilot.sln
python -m unittest discover -s tests/reader_tests -p 'test_*.py' -v
```

## 产品设计

- [设计文档导航](docs/README.md)
- [总体架构与模块边界](docs/design/WeChat_AI_Copilot_Module_Design_v1/Overall_Architecture_Reference.md)
- [跨模块共享契约](docs/design/WeChat_AI_Copilot_Module_Design_v1/00_Shared_Contracts.md)
- [并行开发计划](docs/design/WeChat_AI_Copilot_Module_Design_v1/Parallel_Development_Plan.md)
