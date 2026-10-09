# 微信 AI 沟通助手

这是正式产品开发仓库。实现从零开始；技术验证项目位于工作区中的独立仓库 [`poc-reference`](../poc-reference/README.md)，不属于本仓库的源码、测试、数据库或构建依赖。

## 当前状态

- 解决方案：[WeChatAiCopilot.sln](WeChatAiCopilot.sln)
- API：[src/WeChatAiCopilot.Api](src/WeChatAiCopilot.Api)
- 当前只提供 `GET /health/live` 存活检查。
- 业务模块、数据库、正式读取器、前端和自动化测试尚未实现。

从本仓库根目录启动空宿主：

```powershell
dotnet run --project src/WeChatAiCopilot.Api
```

## 产品设计

- [设计文档导航](docs/README.md)
- [总体架构与模块边界](docs/design/WeChat_AI_Copilot_Module_Design_v1/Overall_Architecture_Reference.md)
- [跨模块共享契约](docs/design/WeChat_AI_Copilot_Module_Design_v1/00_Shared_Contracts.md)
- [并行开发计划](docs/design/WeChat_AI_Copilot_Module_Design_v1/Parallel_Development_Plan.md)

正式代码按 S0 契约评审与冻结后进入 V0.1。系统只读采集；AI 输出供用户参考，微信消息始终由用户手动发送。
