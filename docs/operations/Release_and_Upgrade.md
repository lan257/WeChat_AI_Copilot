# 发布与升级 · Windows 包

面向两个读者：把软件交给别人的人（维护者），和第一次装到陌生电脑上的使用者。
只读采集、不代发消息的边界在所有步骤里都不变。

## 1. 维护者：生成一个可交付的包

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-ReleasePackage.ps1
```

产出在 `artifacts\release\`，交给别人时**三样一起给**：

| 文件 | 作用 |
| --- | --- |
| `WeChatAiCopilot-<版本>-win-x64.zip` | 安装包：自包含应用 + 启动脚本 + 只读组件 + `db\migrations` |
| `…zip.sha256` | 安装包哈希，安装脚本会核对 |
| `Install-FromPackage.ps1` | 安装入口（放在包外，因为对方手上先只有包） |

`RELEASE.txt`、`release-manifest.json`、`SHA256SUMS.txt`、`VERSION.txt` 都在包里，是给对方核对用的。

脚本自己会做这些检查，任一不通过就中止：

- 应用必须以自包含方式生成（目标机不需要装 .NET）。
- 包内必须包含 `启动正式项目.cmd`、`scripts\Start-FormalProject.ps1`、`scripts\Setup-TrialMachine.ps1`、
  `scripts\Install-FromPackage.ps1`、`src\WeChatAiCopilot.Reader\reader.py`、应用可执行文件与入口页。
- 入口页必须指向 `/v8/main.js`（防止发出旧界面）。
- 加 `-SkipPublish` 时会比较源码与已生成应用的修改时间，**源码更新过就直接拒绝**，避免把昨天的程序发出去。

版本号目前只是构建身份（`0.0.0-<日期-时间>+<git 短哈希>`），项目还没有正式的语义化版本方案；
`release-manifest.json` 里 `signed: false` 是明确的自我声明。

## 2. 使用者：装到陌生电脑上

把三样文件放进同一个目录，在该目录执行：

```
powershell -NoProfile -ExecutionPolicy Bypass -File Install-FromPackage.ps1 -PackageZip "WeChatAiCopilot-<版本>-win-x64.zip" -RunSetup
```

安装脚本按顺序做四件事，任何一步失败都会停下并说明原因：

1. 核对安装包 SHA-256；`.sha256` 缺失或值不一致就拒绝安装（`-SkipHashCheck` 只给明知情况的人）。
2. 解压到临时目录，逐个文件按 `release-manifest.json` 核对哈希，任何一项不符就报告“包可能损坏或被截断”。
3. 检查目标目录里没有本程序正在运行的进程，有就提示先双击「关闭正式项目」。
4. 镜像复制到 `%LOCALAPPDATA%\Programs\WeChatAiCopilot`，随后 `-RunSetup` 会接着准备 PostgreSQL、
   Python 只读组件与桌面快捷方式（复用 `Setup-TrialMachine.ps1`，没有第二套安装逻辑）。

聊天数据不在安装目录里，而在 `%LOCALAPPDATA%\WeChatAiCopilot\Formal`（含私有数据库、备份、凭据），
**升级和重装都不会动它**。之后日常只需双击桌面「启动正式项目」。

## 3. 升级

1. 维护者重新生成包，把新包发给对方。
2. 对方先双击「关闭正式项目」，再执行第 2 节的安装命令（同一目录会被镜像更新）。
3. 首次启动时如果检测到新的数据库迁移，启动器会自动：完整备份 → 把备份还原到临时库并比对行数（恢复演练）→
   通过才应用迁移，失败则中止启动并保留原库。备份保留最近 10 份在 `Formal\backups`。
4. 想要手动备份或演练：`powershell -File scripts\Backup-FormData.ps1 -Verify -Keep 10`。

## 4. 回滚

- **应用回滚**：保留上一版的 zip，按第 2 节重新安装即可（数据目录不受影响）。
- **数据库回滚**：`Formal\backups\formal-<时间戳>.dump` 是升级前的完整导出，
  恢复步骤见 `docs\operations\Local_Operations_Guide.md` 第 6 节；`022`、`023` 这类迁移本身是加法，
  回滚 = 恢复升级前的 dump，不需要反向迁移脚本。

## 5. 没有签名，也没有假装签名

这是当前最大的发行门槛缺口，必须如实告诉使用者：

- 包与安装脚本都**未代码签名**。陌生电脑上第一次运行会出现 SmartScreen「已保护你的电脑」提示，
  因为发布者信誉未知。
- 正确做法：保持上面的核对流程（哈希来自维护者本人、通过可信渠道转达），在提示页选择「更多信息」→「仍要运行」。
  不要为了绕过提示而关闭 Windows 安全功能，也不要修改组策略。
- 真正的解决方案是购买代码签名证书并对 zip 与 exe 签名，或改走 MSIX/商店分发。这两项都还没做，
  在做出之前，请把这套包当作“熟人之间、可核对哈希的本机软件”，不是公开渠道下载。

## 6. 仍然没有做到的（别对外宣称）

- 没有自动更新通道；每次升级都要重新安装。
- 没有字节级可复现构建（时间戳与压缩会变化），可复现的是**每个文件的 SHA-256 清单**，不是 zip 本身。
- 没有干净 VM 上的完整冒烟；CI 会在 Windows runner 上真实“打包 → 安装 → 无数据库启动 → 检查资源头”，
  但不覆盖微信登录状态、只读组件在真实微信上的表现与桌面快捷方式的视觉结果。
- 首次真正运行仍需要使用者在微信里已登录，并确认一次监听联系人。
