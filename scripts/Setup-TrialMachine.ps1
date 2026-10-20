# One-shot preparation for a trial machine.
#
# Everything here is idempotent and safe to re-run. It never deletes chat data and never touches an
# existing database. Run it once per machine; after that the user only double-clicks 启动正式项目.cmd.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Setup-TrialMachine.ps1 -Yes
param(
    [switch]$Yes,
    [switch]$SkipInstallers,
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$productRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $productRoot 'src\WeChatAiCopilot.Api'
$readerRoot = Join-Path $productRoot 'src\WeChatAiCopilot.Reader'
$appDir = Join-Path $productRoot 'artifacts\app\win-x64'
$publishedExe = Join-Path $appDir 'WeChatAiCopilot.Api.exe'
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
$setupLog = Join-Path $stateRoot 'setup.log'
$requiredReaderVersion = '1.2.7.0'
$results = @()

function Step([string] $name, [string] $state, [string] $detail) {
    $script:results += [pscustomobject]@{ Step = $name; State = $state; Detail = $detail }
    $suffix = ''
    if ($detail) { $suffix = ' - ' + $detail }
    Write-Host ('[{0}] {1}{2}' -f $state, $name, $suffix)
}

function Ask([string] $question) {
    if ($Yes) { return $true }
    $answer = Read-Host ($question + ' [y/N]')
    return $answer -match '^[yY]'
}

function Get-Winget {
    $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-ReaderRuntime {
    $modulePath = Join-Path $readerRoot '.runtime\package\wechatauto\db.py'
    $metadataPath = Join-Path $readerRoot '.runtime\package\UPSTREAM.txt'
    $cryptoPath = Join-Path $readerRoot '.runtime\deps\cryptography\__init__.py'
    $zstandardPath = Join-Path $readerRoot '.runtime\deps\zstandard\__init__.py'
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $cryptoPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $zstandardPath -PathType Leaf)) { return $false }
    try {
        $metadata = [System.IO.File]::ReadAllText($metadataPath)
        return $metadata.Contains('PyPI release: ' + $requiredReaderVersion)
    } catch { return $false }
}

Write-Host '知间 · 试用机一键准备'
Write-Host ('项目目录：' + $productRoot)
Write-Host ''

if (-not (Ask '开始准备这台电脑？')) { throw '已取消。' }
New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null

# --- 1. platform -------------------------------------------------------------
if ([Environment]::Is64BitOperatingSystem) { Step '操作系统 64 位' 'OK' '' }
else { Step '操作系统 64 位' 'FAIL' '只支持 64 位 Windows'; throw '需要 64 位 Windows。' }

# --- 2. PostgreSQL 18 --------------------------------------------------------
function Find-Postgres18 {
    $service = Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'" -ErrorAction SilentlyContinue
    if ($service -and $service.PathName -match '(?i)([A-Z]:\\.*?\\pg_ctl\.exe)') {
        $dir = Split-Path -Parent $Matches[1]
        if (Test-Path (Join-Path $dir 'initdb.exe')) { return $dir }
    }
    foreach ($dir in @('G:\PostgreSQL\18\bin', (Join-Path ${env:ProgramFiles} 'PostgreSQL\18\bin'), 'C:\Program Files\PostgreSQL\18\bin')) {
        if (Test-Path (Join-Path $dir 'initdb.exe')) { return $dir }
    }
    return $null
}

$pgBin = Find-Postgres18
if ($pgBin) { Step 'PostgreSQL 18' 'OK' $pgBin }
else {
    $winget = Get-Winget
    if ($SkipInstallers -or -not $winget) {
        Step 'PostgreSQL 18' 'FAIL' '未安装，且无法自动安装'
        throw '请安装 PostgreSQL 18（winget install -e --id PostgreSQL.PostgreSQL.18）后重跑本脚本。'
    }
    Step 'PostgreSQL 18' 'RUN' '正在静默安装，可能需要几分钟…'
    & $winget install -e --id PostgreSQL.PostgreSQL.18 --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-File -FilePath $setupLog -Append -Encoding utf8
    $pgBin = Find-Postgres18
    if (-not $pgBin) { Step 'PostgreSQL 18' 'FAIL' '安装后仍未找到，请重跑本脚本' ; throw 'PostgreSQL 18 安装未完成。' }
    Step 'PostgreSQL 18' 'OK' $pgBin
}

# --- 3. Python 3.11 (64-bit) -------------------------------------------------
function Test-Python311 {
    $probe = 'import sys,struct; print(struct.calcsize("P")*8, sys.version_info[1]); sys.exit(0 if sys.version_info[:2] == (3,11) and struct.calcsize("P") == 8 else 1)'
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        try {
            $out = & $launcher.Source '-3.11' '-I' '-c' $probe 2>$null
            if ($LASTEXITCODE -eq 0 -and ($out -join '').Trim() -eq '64 11') { return $true }
        } catch { }
    }
    foreach ($exe in @((Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'), (Join-Path ${env:ProgramFiles} 'Python311\python.exe'))) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
        try {
            $out = & $exe '-I' '-c' $probe 2>$null
            if ($LASTEXITCODE -eq 0 -and ($out -join '').Trim() -eq '64 11') { return $true }
        } catch { }
    }
    return $false
}

if (Test-Python311) { Step 'Python 3.11 (64 位)' 'OK' '' }
else {
    $winget = Get-Winget
    if ($SkipInstallers -or -not $winget) {
        Step 'Python 3.11 (64 位)' 'FAIL' '未安装，且无法自动安装'
        throw '请安装 64 位 Python 3.11（winget install -e --id Python.Python.3.11）后重跑本脚本。'
    }
    Step 'Python 3.11 (64 位)' 'RUN' '正在静默安装…'
    & $winget install -e --id Python.Python.3.11 --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-File -FilePath $setupLog -Append -Encoding utf8
    if (-not (Test-Python311)) { Step 'Python 3.11 (64 位)' 'FAIL' '安装后仍未找到，请重跑本脚本'; throw 'Python 3.11 安装未完成。' }
    Step 'Python 3.11 (64 位)' 'OK' ''
}

# --- 4. read-only WeChat module ---------------------------------------------
$readerModule = Join-Path $readerRoot '.runtime\package\wechatauto\db.py'
if (Test-ReaderRuntime) {
    Step '只读采集组件' 'OK' ('固定版本 ' + $requiredReaderVersion + ' 已就绪')
}
else {
    if (-not (Ask '需要下载只读采集组件（访问 PyPI），继续？')) { throw '未准备只读采集组件。' }
    Step '只读采集组件' 'RUN' '正在下载并做只读完整性校验…'
    & (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $readerRoot 'prepare-reader.ps1') 2>&1 |
        Out-File -FilePath $setupLog -Append -Encoding utf8
    if (-not (Test-ReaderRuntime)) { Step '只读采集组件' 'FAIL' '版本或文件校验未通过；见 setup.log'; throw '只读采集组件准备失败。' }
    Step '只读采集组件' 'OK' ''
}

# --- 5. the app itself: prebuilt, or build it once here ---------------------
if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
    Step '应用本体' 'OK' '使用已随包提供的自包含版本，无需安装 .NET'
}
else {
    $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) {
        Step '应用本体' 'FAIL' '既没有预构建产物，也没有 .NET SDK'
        throw '请在开发电脑上运行 scripts\Setup-TrialMachine.ps1 生成 artifacts\app\win-x64，并把整个项目目录复制过来。'
    }
    Step '应用本体' 'RUN' '正在生成自包含版本（约 1–3 分钟）…'
    & $dotnet 'publish' $apiProject '-c' 'Release' '-r' 'win-x64' '--self-contained' 'true' '-o' $appDir '--nologo' '-v' 'q' 2>&1 |
        Out-File -FilePath $setupLog -Append -Encoding utf8
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { Step '应用本体' 'FAIL' '见 setup.log'; throw '自包含版本生成失败。' }
    Step '应用本体' 'OK' 'artifacts\app\win-x64'
}

# --- 6. desktop shortcuts ----------------------------------------------------
if (-not $NoShortcuts) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell
    $shortcuts = @(
        @{ name = '知间 启动'; target = (Join-Path $productRoot '启动正式项目.cmd') },
        @{ name = '知间 停止'; target = (Join-Path $productRoot '关闭正式项目.cmd') }
    )
    foreach ($item in $shortcuts) {
        $link = Join-Path $desktop ($item.name + '.lnk')
        if (-not (Test-Path -LiteralPath $item.target)) { Step ('快捷方式：' + $item.name) 'SKIP' '目标不存在'; continue }
        $lnk = $shell.CreateShortcut($link)
        $lnk.TargetPath = $item.target
        $lnk.WorkingDirectory = $productRoot
        $lnk.Description = '知间 微信 AI 沟通助手（本机试用）'
        $lnk.Save()
        Step ('快捷方式：' + $item.name) 'OK' $desktop
    }
}

# --- 7. optional local proxy hint -------------------------------------------
$networkPath = Join-Path $stateRoot 'network.json'
if (Test-Path -LiteralPath $networkPath) { Step '本机代理配置' 'OK' 'network.json 已存在' }
else { Step '本机代理配置' 'SKIP' '按直连启动；若 AI 任务总超时再按教程 §4 添加 network.json' }

$codexCli = Get-Command codex.exe, codex.cmd, codex -ErrorAction SilentlyContinue | Select-Object -First 1
if ($codexCli) { Step '产品内置 AI Agent' 'OK' '已找到 Codex CLI；请按部署手册单独确认登录状态' }
else { Step '产品内置 AI Agent' 'WARN' '未找到 Codex CLI；工作区可启动，AI 建议需先安装并登录 Codex CLI' }

Write-Host ''
Write-Host '准备完成。接下来：'
Write-Host '  1) 打开并登录微信桌面版，停在聊天窗口；'
Write-Host '  2) 如需 AI 建议，按 Agent_Install_Runbook.md 安装并登录 Codex CLI；'
Write-Host '  3) 双击桌面「知间 启动」；'
Write-Host '  4) 按提示由使用者输入编号，选择要授权的那一个联系人；'
Write-Host '  5) 浏览器会自动打开工作区。'
Write-Host ''
Write-Host '以后每次使用只需双击「知间 启动」，不用重复本脚本。'
