# M1 本机只读 Reader

本模块是正式项目里的 Windows 本机只读读取器。它连接一个当前活动微信账号，可同时监听服务端明确授权的最多 20 个普通一对一会话。实时消息为 `RawMessageEventV1`，历史读取为有界的 `HistoryBatchV1`。Reader 每 5 秒同步一次监听目标并发送运行心跳；Web 当前查看的会话不会改变监听集合。

读取器只使用 `wechatauto-replica==1.2.5.1` 的 `wechatauto/db.py` 数据库读取模块。准备脚本会验证 wheel 内文件的 SHA-256，并且只解出数据库模块和无操作 logger；不安装或导入发送、UI、CLI、媒体或 Moments 模块。读取器禁用持久化密钥保存和跨账号回退。消息数据库解密缓存和临时密钥路径放在系统临时目录，进程结束时清理。

## 首次准备

要求 Windows、Python 3.11（`py -3.11`）和可访问 PyPI。PowerShell 从本目录运行：

```powershell
.\prepare-reader.ps1
```

依赖和只读数据库模块只安装在本模块的 `.runtime` 目录，该目录已加入 `.gitignore`。

正式项目首次启动且本机尚无已确认会话时，启动器会要求输入一个一对一联系人显示名，并调用 Reader 的内部 `resolve-scope` 初始化入口。此入口只在本机联系人表中精确查找该名称，不读取聊天正文、不列出其他联系人，也不会在名称不存在或有多个匹配时保存账号/会话范围。成功后范围配置写入当前 Windows 用户的受限 LocalAppData；后续启动按该配置连接。

## 配置

复制 `settings.example.json` 到 `%LOCALAPPDATA%\WeChatAiCopilot\Reader\settings.json`，将 `enabled` 改为 `true`，并填入本机活动微信账号外部 ID。启用正式 API 传输后，Reader 从服务器读取监听目标；本机 `conversationExternalId` / `conversationExternalIds` 只用于兼容离线输出与历史读取。读取器不接受外部指定的数据库路径、SQL 或 Shell 命令。

若要直接提交正式 API，在此本机 settings 文件的 `transport` 中设置 `enabled`、正式 API 根地址和 M6 配置的同一个 Reader Bearer token。token 只放在 LocalAppData 的 settings 文件中，不要复制到仓库文件、命令行或日志。远程 API 必须使用 HTTPS；仅 loopback 地址可使用 HTTP。传输关闭时，契约 JSONL 仍输出到标准输出。传输开启时，JSON 正文不会写到标准输出；成功只报告契约类型和 HTTP 状态，失败报告 HTTP 状态及经过过滤的错误码，不输出响应正文、URL 或 token。

在真实数据读取前，操作者需确认账号和会话授权。Reader 只接受同一个当前活动账号下用户明确选择的 Direct 会话，最多 20 个；不会订阅所有会话或群聊。浏览器请求“发现会话”后，Reader 只上传会话外部 ID 和本机联系人显示名，不上传摘要、最后发言人或消息正文。监听目标变化不会重启 Reader；每个目标使用独立持久水位，数据保存在 LocalAppData。首次添加监听目标时以上游监听器当前最新消息为起点；历史补录仍是单独的有界命令。

## 使用

实时采集输出一条 JSON Lines 事件后持续运行，按 Ctrl+C 停止：

```powershell
py -3.11 .\reader.py realtime
```

历史补录按 UTC 范围读取文本，最多 5,000 条、每批最多 500 条。时间边界包含起止时刻；时间参数可以省略，但总行数仍有硬上限：

```powershell
py -3.11 .\reader.py history --job-id local-check-001 --from-utc 2026-01-01T00:00:00Z --to-utc 2026-10-01T00:00:00Z --max-rows 5000 --batch-size 100
```

命令行标准输出只包含契约 JSON。失败信息只写入标准错误的稳定错误码，不写入消息正文、账号/会话 ID、源 ID 或本地路径。历史读取遇到分片错误或触及上限时会失败且不输出批次，避免把部分结果报告为完成。

配置传输时，Reader 使用 `/api/reader/targets` 同步授权目标，向 `/api/reader/status` POST 心跳，并将实时事件和历史批次提交至原 Reader ingestion API。用户显式请求发现会话后，Reader 从 `/api/reader/discovery-jobs/next` 领取一次任务，再仅提交会话目录元数据。认证头为 `Authorization: Bearer ...`。请求不会跟随 HTTP 重定向或切换到其它服务。遇到暂时性网络错误、408、425、429 或 5xx 最多重试三次；永久 HTTP 拒绝会返回脱敏错误码。

## 当前范围与契约限制

- 所有事件的 `direction` 固定为 `Unknown`，`directionEvidence` 固定为 `Unresolved`；不会猜测或使用方向校准。
- `externalMessageId` 对具有 `localId` 和 `sortSeq` 的行使用现有 `wx4:` SHA-256 规则。跨 shard 唯一性尚未证明；原始 `serverId`、表名和稳定 shard 指纹分别保留。
- 无法解析的来源时间作为 `null` 输出；带时间范围的历史任务只返回可比较的有效时间行。
- 当同一 `localId`/`sortSeq` 在多个 shard 命中时，保留该复合键；对不一致的 shard 或 `serverId` 值不作猜测，相关字段置空。
- D02 当前没有截断、扫描 shard 计数或分片错误字段。为避免静默部分成功，本模块对读取错误和超限采取整体失败；后续如需可恢复进度/部分批次，应先扩展共享历史任务契约。
- Reader 心跳反映本机 Reader 进程与监听器状态，不代表微信账号一定没有登出；离线账户状态仍以心跳超时标记。网络断线期间暂不提供跨机器持久化消息队列；Reader 事件提交失败会报告错误并停止本轮监听，避免把未提交事件显示成已同步。
- 真实运行时多会话行为仍需在用户明确选择的同一活动账号下验收；不能以本地 Listener 单元行为代替真实微信版本上的双会话验收。
