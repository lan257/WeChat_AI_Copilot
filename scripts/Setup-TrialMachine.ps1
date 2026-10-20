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
. (Join-Path $readerRoot 'ReaderRuntime.ps1')

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

function Test-ReaderRuntime([string] $pythonPath) {
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
        return $metadata.Contains('PyPI release: ' + $requiredReaderVersion) -and
            (Test-ReaderRuntimeCompatibility $pythonPath $readerRoot)
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

# --- 3. Python (64-bit, 3.11 preferred) --------------------------------------
$pythonCandidates = @(Get-ReaderPythonCandidates)
if ($pythonCandidates.Count -eq 0) {
    $winget = Get-Winget
    if ($SkipInstallers -or -not $winget) {
        Step 'Python 3.x (64 位)' 'FAIL' '未找到可用版本，且无法自动安装'
        throw '未找到可用的 64 位 Python 3.10 或更高版本。请安装 Python 3.11（推荐）或其他 3.10+ 版本后重跑。'
    }
    Step 'Python 3.11 (推荐)' 'RUN' '未检测到可用 Python，正在静默安装…'
    & $winget install -e --id Python.Python.3.11 --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-File -FilePath $setupLog -Append -Encoding utf8
    $pythonCandidates = @(Get-ReaderPythonCandidates)
    if ($pythonCandidates.Count -eq 0) { Step 'Python 3.x (64 位)' 'FAIL' '安装后仍未找到，请重跑本脚本'; throw 'Python 安装后仍未检测到可用的 64 位 Python 3.10 或更高版本。' }
}

# --- 4. read-only WeChat module ---------------------------------------------
$readerModule = Join-Path $readerRoot '.runtime\package\wechatauto\db.py'
$selectedPython = $null
foreach ($candidate in $pythonCandidates) {
    if (Test-ReaderRuntime $candidate.Path) { $selectedPython = $candidate; break }
}
if (-not $selectedPython) { $selectedPython = $pythonCandidates[0] }

if (Test-ReaderRuntime $selectedPython.Path) {
    Step ('Python ' + $selectedPython.Version + ' (64 位)') 'OK' 'Reader 依赖导入自检通过；3.11 为推荐版本'
    Step '只读采集组件' 'OK' ('已校验版本 ' + $requiredReaderVersion + '，本机依赖可用')
}
else {
    if (-not (Ask ('当前 Python ' + $selectedPython.Version + ' 无法直接加载随包 Reader 依赖；需要访问 PyPI 为此版本重新准备并自检，继续？'))) {
        throw ('当前 Python ' + $selectedPython.Version + ' 与随包 Reader 本机依赖不兼容。可允许 Setup 访问 PyPI 重新准备，或安装 Python 3.11（推荐）后重跑。')
    }
    Step ('Python ' + $selectedPython.Version + ' Reader 兼容性') 'RUN' '正在准备依赖并做只读完整性与导入校验…'
    $prepareScript = Join-Path $readerRoot 'prepare-reader.ps1'
    & (Get-Command powershell.exe).Source -NoProfile -ExecutionPolicy Bypass -File $prepareScript -PythonPath $selectedPython.Path 2>&1 |
        Out-File -FilePath $setupLog -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-ReaderRuntime $selectedPython.Path)) {
        Step '只读采集组件' 'FAIL' '依赖或导入校验未通过；见 setup.log'
        throw ('Reader 无法在 Python ' + $selectedPython.Version + ' 下运行。请检查 setup.log 中 PyPI/依赖错误；可改用 Python 3.11（推荐）后重试。')
    }
    Step ('Python ' + $selectedPython.Version + ' (64 位)') 'OK' 'Reader 依赖导入自检通过'
    Step '只读采集组件' 'OK' ('已校验版本 ' + $requiredReaderVersion)
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
