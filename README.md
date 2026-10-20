# WeChat AI Copilot

一个面向 Windows 本机微信数据的 AI 助手与工作区项目，旨在将微信聊天记录、历史上下文、记忆和 AI 建议/咨询能力整合到一个统一的本地应用中。

该仓库是正式产品的独立实现仓库，包含：

- 只读微信数据采集与归档模块（Reader）
- ASP.NET Core API 服务
- 会话上下文与记忆能力
- AI 建议与咨询能力
- Web 工作区界面与本地桌面启动入口
- 设计文档、模块契约与测试

> 说明：本项目仅做只读采集，不提供自动发送或自动回复微信消息。

## 项目定位

WeChat AI Copilot 以“本地数据 + AI 助手”作为核心思路，支持以下能力：

- 从本机微信数据中采集消息与历史批次
- 识别会话、账号、发送者方向和历史上下文
- 构建会话记忆、证据、人物画像和上下文窗口
- 结合 AI 生成建议、咨询和工作流辅助
- 在本地 Web 工作区中查看消息、记忆和建议结果

## 模块概览

### 1. Reader

Reader 负责读取本地微信数据，并以结构化 JSON Lines 形式提交给 API 或持久化中间结果。

主要职责：

- 读取微信消息和历史批次
- 识别账号、会话、消息方向
- 进行数据清洗与标准化
- 以只读、可审计方式写入系统

### 2. API

API 层使用 ASP.NET Core 作为正式产品主机，提供：

- 读者/消息导入接口
- 会话与账号查询接口
- 上下文和记忆管理接口
- AI 建议与咨询接口
- 健康检查与系统状态接口

### 3. Context / Memory

负责处理历史上下文、人工记忆、证据校验，以及基于会话的上下文窗口管理。

### 4. Agent

AI 代理层负责：

- 按会话范围协调多个候选与轮次
- 生成建议和咨询响应
- 持久化运行结果和审计信息
- 支持本地桌面启动配置 Codex CLI 运行时

### 5. Workspace

Web 工作区提供：

- 双栏消息与上下文界面
- 极简模式
- 人设切换
- 建议复制
- 历史与上下文查询

### 6. Platform

平台公共能力包括：

- Bearer 鉴权
- PostgreSQL 迁移与初始化
- 健康/就绪检查
- Outbox 消费与重试机制

## 技术栈

- C# / ASP.NET Core
- Python（Reader）
- PostgreSQL
- HTML / JavaScript / CSS
- .NET CLI / PowerShell / Windows 批处理脚本

## 仓库结构

```text
.
├── .github/
├── db/
├── docs/
├── scripts/
├── src/
│   ├── WeChatAiCopilot.Api/
│   └── WeChatAiCopilot.Reader/
├── tests/
│   ├── WeChatAiCopilot.AgentTests/
│   ├── WeChatAiCopilot.DomainTests/
│   ├── WeChatAiCopilot.MemoryTests/
│   ├── WeChatAiCopilot.PlatformTests/
│   └── reader_tests/
├── README.md
├── WeChatAiCopilot.sln
├── Copy-WebToken.cmd
├── 启动正式项目.cmd
├── 关闭正式项目.cmd
├── .gitignore
└── ...
```

## 快速开始

### 1. 安装依赖

确保本机已安装：

- .NET SDK
- PostgreSQL（按需）
- Python 3
- Windows 环境（该项目面向本机 Windows 运行）

### 2. 启动 API

在仓库根目录执行：

```powershell
dotnet run --project src/WeChatAiCopilot.Api
```

默认情况下，应用可在无数据库配置时启动；但当设置 `ConnectionStrings__FormalDatabase` 后，启动时会自动应用仓库中的版本化迁移。

### 3. 健康检查

```http
GET /health/ready
```

返回就绪状态可以用于确认数据库与依赖项状态。

### 4. 关键配置

常见配置项包括：

- `ConnectionStrings__FormalDatabase`
- `Authentication__WebBearerToken`
- `Authentication__ReaderBearerToken`
- `Reader__AllowedAccountExternalId`
- `Web__AllowedAccountExternalId`
- `Agent:Runtime`

其中：

- `Agent:Runtime` 可设置为 `Fake` 或 `CodexCli`
- 未配置或未知值时，Agent 将默认失败关闭
- 使用 Codex CLI 时，需要单独配置目标机运行时环境

## API 说明

### 健康与系统状态

- `GET /health/live`
- `GET /health/ready`
- `GET /api/system/status`

### Reader 与数据导入

- `POST /api/reader/messages`
- `POST /api/reader/history-batches`
- `GET /api/reader/targets`
- `POST /api/reader/status`

### 会话与账号

- `GET /api/accounts`
- `GET /api/accounts/{accountId}/conversations`
- `GET /api/conversations/current`
- `GET /api/conversations/current/messages`

### 记忆与上下文

- `GET /api/conversations/current/memory`
- `POST /api/conversations/current/memory`
- `PUT /api/conversations/current/memory/{memoryId}`
- `DELETE /api/conversations/current/memory/{memoryId}`

### AI 建议与咨询

- `GET /api/ai/personas`
- `GET /api/ai/execution-profiles`
- `POST /api/ai/suggestions`
- `POST /api/ai/consultations`
- `GET /api/ai/consultations/{threadId}`
- `POST /api/ai/consultations/{threadId}/turns`

## 测试

项目提供 `.NET` 和 `Python` 两类测试：

```powershell
dotnet build WeChatAiCopilot.sln
dotnet test WeChatAiCopilot.sln
python -m unittest discover -s tests/reader_tests -p 'test_*.py' -v
```

测试覆盖范围包括：

- Agent 逻辑
- Domain 规则
- Memory 处理
- Platform 能力
- Reader 数据结果

## 文档

项目文档集中在 `docs/` 目录，其中包括：

- 设计与总体架构
- 模块契约
- 并行开发计划
- 运行手册
- 版本状态说明

关键入口：

- [docs/README.md](docs/README.md)
- [src/WeChatAiCopilot.Api/README.md](src/WeChatAiCopilot.Api/README.md)
- [src/WeChatAiCopilot.Reader/README.md](src/WeChatAiCopilot.Reader/README.md)

## 设计原则

本项目遵循以下设计理念：

- 以本地、只读方式采集微信数据
- 明确模块边界，降低耦合
- 以契约驱动模块协作
- 保持可审计、可验证、可回放的消息处理链路
- AI 能力采用受控执行与预算管理

## 注意事项

- 本项目不会自动发送消息，也不会自动回复微信。
- Agent 默认需要显式配置运行时，避免未授权行为。
- 正式运行前，需要根据环境准备数据库、鉴权和账户范围配置。

## 许可证与维护

该仓库用于正式产品开发与本地验证。若你要在自己的环境中运行和扩展，请优先参考文档目录中的开发与运维说明。

## 结语

WeChat AI Copilot 是一个围绕微信消息、上下文、记忆和 AI 协作的本地工作区型项目。它强调安全、可审计和模块化设计，适合用于研究、原型开发和正式产品迭代。

如果你希望，我也可以继续把这个 README 再调整成更偏“GitHub 风格展示页”的版本，或者改成更适合中文开源项目的“项目介绍 + 运行指南 + 贡献说明”版本。
