# 给朋友装「知间」· 新电脑版

这份指南给维护者和收件人。新电脑请用经过校验的 Windows 发布包；不要把整个源码仓库当安装包复制过去。

## 交给朋友和新 Agent 的文件

从同一个发布目录提供四个文件：

1. WeChatAiCopilot-<版本>-win-x64.zip
2. 与 zip 同名的 .zip.sha256
3. Install-FromPackage.ps1
4. Agent_Install_Runbook.md

把这四个文件都附给朋友使用的操作型 AI Agent，并给它这句话：

> 请按附件 Agent_Install_Runbook.md 在这台电脑上安装并逐项验收。请在当前用户权限下直接执行命令，不要让我复制粘贴。只在需要我本人选择联系人、决定本机历史补录、确认 AI 数据用途或完成账号登录时询问我。出现哈希错误、旧数据或管理员要求时先停止并说明原因。

Agent 需能读取附件、运行普通用户 PowerShell 并查看浏览器结果。若它没有本机执行能力，请让维护者换用可操作本机的 Agent；不要假装已经安装或通过验收。

## 朋友自己安装

前提是 64 位 Windows、Windows PowerShell 5.1、winget/App Installer 和已安装的微信桌面版。安装程序可通过 winget 安装缺少的 PostgreSQL 18、Python 3.11；PostgreSQL 是机器范围安装，Windows 可能弹出 UAC，需朋友本人批准。发布包自带自包含应用、固定版 Reader runtime 和 Agent 安装手册，不需要 .NET SDK 或 PyPI 网络。

把四个文件放入同一目录，在该目录打开 PowerShell 并执行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./Install-FromPackage.ps1 -PackageZip "./WeChatAiCopilot-<版本>-win-x64.zip" -RunSetup
~~~

SHA-256 或包内清单核对失败时立即停止，不要使用跳过校验参数。若 Windows 为 PostgreSQL 显示 UAC，请由朋友本人确认；无管理员权限或未批准时无法完成全新安装。Setup 成功后检查“准备完成。”，并确认没有 PostgreSQL、Python 或 Reader runtime 错误。

## 首次启动与产品内置 AI

1. 先打开并登录微信，停在聊天窗口。
2. 首次启动会列出 Direct 联系人；由朋友本人选择一个编号，Agent 不得代选。
3. 首次启动默认把该会话最多 5,000 条可读文本消息补录到本机私有数据库。若不允许本机补录，Agent 可用 Runbook 中的跳过历史参数启动 Reader 实时监听。
4. 生成 AI 建议还需要单独安装并登录本机 Codex CLI。登录由朋友自己完成。没有 CLI 或未登录时，工作区仍可运行，但产品内置 AI Agent 尚不可用。
5. 真实会话上下文只有在朋友明确同意后才用于 AI 建议；内容交给朋友登录的 Codex CLI/OpenAI 服务处理。软件不会发送微信消息，也没有自动发送功能。

## 怎么判断装好了

分开检查两个状态：

- **工作区与 Reader 就绪**：本机健康检查为 200，浏览器打开工作区，获准联系人出现，Reader 显示“监听中”。
- **产品内置 AI Agent 就绪**：Codex CLI 已安装、登录状态正常；只有朋友同意用选定会话做一次 AI 请求后，才核对三条非空建议。没有同意就记为“未执行”，不要让 Agent 生成真实内容来证明安装。

最终关闭时双击桌面“知间 停止”。若端口 5080 或 55432 仍监听，先确认占用进程，不要强杀。

## 隐私与问题反馈

消息与记忆保存在朋友自己的电脑。产品 AI 请求会把当前获准会话的必要上下文发送给用户登录的 Codex CLI/OpenAI 服务；不使用 AI 时，部署 Agent 不会把聊天正文发送给外部服务。不要截图或转发聊天记录。

遇到失败，请按附件 Runbook 的错误表回报：命令、错误码、期望与实际结果、Windows/PowerShell/Python/PostgreSQL/Codex CLI 版本。不要提供消息正文、wxid、令牌、数据库路径或 Codex 认证文件。
