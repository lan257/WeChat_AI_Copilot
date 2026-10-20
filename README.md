# WeChat AI Copilot

这是正式产品的独立实现仓库，不是一个“开箱即用”的示例项目。实际运行需要 Windows 本机环境、已登录的微信桌面端、 PostgreSQL、Python、可用的 Codex CLI（若开启内置 AI 建议），并按正式部署文档完成授权与运行配置。

> 先读正式部署文档，不要按“普通 Demo”方式直接启动。
>
> - 正式部署：`docs/operations/Trial_Deployment_Guide.md`
> - 本机安装与验收：`docs/operations/Agent_Install_Runbook.md`
> - 本机开发/运行：`docs/operations/Local_Operations_Guide.md`

## 项目定位

WeChat AI Copilot 是一个面向 Windows 本机微信数据的 AI 辅助工作区，目标是将：

- 微信消息采集
- 会话与历史上下文
- 人工记忆与证据管理
- AI 建议与咨询
- 本地 Web 工作区

整合到一个可审计、可授权、可本地运行的产品中。

本仓库包含：

- `src/WeChatAiCopilot.Api`：ASP.NET Core API 与 Web 工作区
- `src/WeChatAiCopilot.Reader`：本机只读 Reader
- `docs/`：架构设计、模块契约、部署与运行文档
- `tests/`：单元/回归测试
- `db/`：数据库迁移与结构化数据资产

## 运行前必须具备的依赖

正式运行至少需要：

- Windows 10/11
- 微信桌面版，并已登录、停留在聊天窗口
- PostgreSQL（本项目运行与迁移依赖）
- Python 3.11（Reader 运行依赖）
- .NET 10 SDK（源码编译/开发时需要）
- Codex CLI（只有在使用产品内置 AI 建议/咨询时才需要）
- 一个经用户授权的微信账号，以及至少一个 Direct 会话（用于识别与本地补录历史）

注意：

- 本项目不提供自动发送或自动回复微信消息能力。
- 直接运行 API 时，Agent 默认失败关闭；若希望使用产品内置 AI，需安装并登录 Codex CLI。
- 首次启动需要由用户明确确认授权范围，不能直接跳过用户选择。

## 正式部署入口

如果你想在真实 Windows 环境中运行产品，请按下面顺序阅读：

1. [给朋友装「知间」· 新电脑版](docs/operations/Trial_Deployment_Guide.md)
   - 适合交付给新电脑用户或操作型 Agent
2. [全新 Windows 环境部署手册（交给本机操作 Agent）](docs/operations/Agent_Install_Runbook.md)
   - 适合安装、授权、验收的详细步骤
3. [本机开发与运行手册](docs/operations/Local_Operations_Guide.md)
   - 适合源码开发者和本地调试

## 快速开始（开发/构建场景）

如果你是在源码环境中做开发和构建，而不是交付正式包，至少需要准备：

```powershell
dotnet --version
dotnet build .\WeChatAiCopilot.sln
dotnet test .\WeChatAiCopilot.sln
python -m unittest discover -s .\tests\reader_tests -p 'test_*.py' -v
```

然后按本机开发手册配置环境变量与数据库：

```powershell
$env:ConnectionStrings__FormalDatabase = '<本机 PostgreSQL 连接字符串>'
$env:Authentication__WebBearerToken = '<随机生成的 Web token>'
$env:Authentication__ReaderBearerToken = '<另一个随机生成的 Reader token>'
$env:Web__AllowedAccountExternalId = '<获准账号外部 ID>'
$env:Web__AllowedConversationExternalId = '<获准 Direct 会话外部 ID>'
$env:Reader__AllowedAccountExternalId = '<同一获准账号外部 ID>'
$env:Reader__AllowedConversationExternalId = '<同一获准 Direct 会话外部 ID>'
dotnet run --project .\src\WeChatAiCopilot.Api
```

检查健康状态：

- `GET http://127.0.0.1:5080/health/live`
- `GET http://127.0.0.1:5080/health/ready`
- 浏览器访问 `http://127.0.0.1:5080/`

## 关键约束

- Reader 仅读本机微信数据，不做自动发送或自动回复。
- 一个账号下可监听的 Direct 会话范围需先明确授权。
- 产品内置 AI 建议需要用户明确同意，并通过已登录的 Codex CLI/OpenAI 服务处理必要上下文。
- 正式运行前必须确认：数据库、鉴权、账号范围、微信登录状态和 Codex 是否已准备好。

## 主要模块

### API

`src/WeChatAiCopilot.Api`：负责：

- Web 工作区
- 账号与会话查询
- 消息和历史导入接口
- 记忆与上下文接口
- AI 建议/咨询接口
- 健康检查与系统状态

### Reader

`src/WeChatAiCopilot.Reader`：负责：

- 读取本机微信数据
- 识别账号、会话、方向和来源
- 按协议提交到 API
- 只读采集并保留可审计记录

### Docs

`docs/` 中负责保存：

- 模块设计与架构说明
- 共享契约
- 并行开发计划
- 部署手册与运维说明

## 目录结构

```text
.
├── .github/
├── db/
├── docs/
│   └── operations/
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

## 文档导航

- [docs/README.md](docs/README.md)
- [docs/operations/Trial_Deployment_Guide.md](docs/operations/Trial_Deployment_Guide.md)
- [docs/operations/Agent_Install_Runbook.md](docs/operations/Agent_Install_Runbook.md)
- [docs/operations/Local_Operations_Guide.md](docs/operations/Local_Operations_Guide.md)
- [src/WeChatAiCopilot.Api/README.md](src/WeChatAiCopilot.Api/README.md)
- [src/WeChatAiCopilot.Reader/README.md](src/WeChatAiCopilot.Reader/README.md)

## 结论

这个仓库不是一个“单机 demo”项目，而是一个需要 Windows 环境、微信授权、PostgreSQL、Python 与 Codex CLI 配合的正式部署型应用。正确的入口不是省略依赖，而是先按正式部署文档完成环境与授权，再进入本机开发或运行流程。
