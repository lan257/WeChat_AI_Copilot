# M1 本机只读 Reader

本模块是正式项目里的 Windows 本机数据读取切片。它从配置的单个普通一对一会话读取消息，并输出与 API `Contracts/V1` 对齐的 JSON Lines：实时为 `RawMessageEventV1`，历史为 `HistoryBatchV1`。传输默认关闭；启用后，实时事件通过 Reader Bearer 鉴权提交到正式 API，历史批次提交至对应的历史批次入口。

读取器只使用 `wechatauto-replica==1.2.5.1` 的 `wechatauto/db.py` 数据库读取模块。准备脚本会验证 wheel 内文件的 SHA-256，并且只解出数据库模块和无操作 logger；不安装或导入发送、UI、CLI、媒体或 Moments 模块。读取器禁用持久化密钥保存和跨账号回退。消息数据库解密缓存和临时密钥路径放在系统临时目录，进程结束时清理。

## 首次准备

要求 Windows、Python 3.11（`py -3.11`）和可访问 PyPI。PowerShell 从本目录运行：

```powershell
.\prepare-reader.ps1
```

依赖和只读数据库模块只安装在本模块的 `.runtime` 目录，该目录已加入 `.gitignore`。

## 配置

复制 `settings.example.json` 到 `%LOCALAPPDATA%\WeChatAiCopilot\Reader\settings.json`，将 `enabled` 改为 `true`，并填入本机活动微信账号和单个 Direct 会话的外部 ID。读取器只从该设置文件读取目标，不接受外部指定的数据库路径、SQL 或 Shell 命令。

若要直接提交正式 API，在此本机 settings 文件的 `transport` 中设置 `enabled`、正式 API 根地址和 M6 配置的同一个 Reader Bearer token。token 只放在 LocalAppData 的 settings 文件中，不要复制到仓库文件、命令行或日志。远程 API 必须使用 HTTPS；仅 loopback 地址可使用 HTTP。传输关闭时，契约 JSONL 仍输出到标准输出。传输开启时，JSON 正文不会写到标准输出；成功只报告契约类型和 HTTP 状态，失败报告 HTTP 状态及经过过滤的错误码，不输出响应正文、URL 或 token。

在真实数据读取前，操作者需确认账号和会话授权。配置只允许一个账号和一个一对一会话；群聊会被拒绝。实时监听以当前数据库最新消息建立基线，不补发基线之前的消息；历史读取使用单独的有界任务。

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

配置传输时，实时事件 POST 到 `/api/reader/messages`，历史批次 POST 到 `/api/reader/history-batches`。认证头为 `Authorization: Bearer ...`。请求不会跟随 HTTP 重定向或切换到其它服务。遇到暂时性网络错误、408、425、429 或 5xx 最多重试三次；永久 HTTP 拒绝会返回脱敏错误码。

## 当前范围与契约限制

- 所有事件的 `direction` 固定为 `Unknown`，`directionEvidence` 固定为 `Unresolved`；不会猜测或使用方向校准。
- `externalMessageId` 对具有 `localId` 和 `sortSeq` 的行使用现有 `wx4:` SHA-256 规则。跨 shard 唯一性尚未证明；原始 `serverId`、表名和稳定 shard 指纹分别保留。
- 无法解析的来源时间作为 `null` 输出；带时间范围的历史任务只返回可比较的有效时间行。
- 当同一 `localId`/`sortSeq` 在多个 shard 命中时，保留该复合键；对不一致的 shard 或 `serverId` 值不作猜测，相关字段置空。
- D02 当前没有截断、扫描 shard 计数或分片错误字段。为避免静默部分成功，本模块对读取错误和超限采取整体失败；后续如需可恢复进度/部分批次，应先扩展共享历史任务契约。
- 读取器尚未实现 ReaderStatus、服务端授权目标同步、历史任务取消或持久化断线队列。配置目标仍由本机 settings 手动指定；正式 API 会再按 M6 token 和 M2 allowlist 校验账号、会话及 provider。
