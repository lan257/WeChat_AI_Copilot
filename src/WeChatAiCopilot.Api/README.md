# WeChatAiCopilot API

这是正式应用的 ASP.NET Core 宿主，独立于技术验证项目。

当前只提供 `GET /health/live` 存活检查，没有业务模块、数据库连接、读取器接入、AI Runtime 或前端。正式数据库迁移与配置在本仓库中从零建立。

系统只读采集微信数据，不提供微信主动发送或自动回复能力。
