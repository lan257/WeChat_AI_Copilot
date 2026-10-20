# Install a 知间 release package on a fresh Windows machine, for a user who is not a developer.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Install-FromPackage.ps1 -PackageZip "C:\Downloads\WeChatAiCopilot-0.8.2-win-x64.zip"
#
# It verifies the published SHA-256, extracts to a per-user directory, mirrors out the previous
# version's files there, and stops if the application is still running. It does not install
# PostgreSQL or Python and does not touch chat data: that stays in %LOCALAPPDATA%\WeChatAiCopilot\Formal,
# which is a different directory. Run scripts\Setup-TrialMachine.ps1 afterwards for the components
# and the desktop shortcuts (one code path, not a second installer).
param(
    [Parameter(Mandatory = $true)][string]$PackageZip,
    [string]$InstallRoot = '',
    [switch]$SkipHashCheck,
    [switch]$RunSetup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$state, [string]$message) {
    Write-Host ('[{0}] {1}' -f $state, $message)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $PackageZip -PathType Leaf)) { throw ('找不到安装包：' + $PackageZip) }
$PackageZip = (Resolve-Path -LiteralPath $PackageZip).Path

# --- 1. integrity ------------------------------------------------------------
$sidecar = $PackageZip + '.sha256'
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackageZip).Hash.ToLowerInvariant()
if (-not $SkipHashCheck) {
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
        throw ('没有校验文件 ' + (Split-Path -Leaf $sidecar) + '。请连同 .sha256 一起复制，或明确使用 -SkipHashCheck。')
    }
    $expectedLine = (Get-Content -Raw -Encoding UTF8 -LiteralPath $sidecar).Trim()
    $expected = ($expectedLine -split '\s+')[0].ToLowerInvariant()
    if ($expected -ne $actual) { throw ('安装包 SHA-256 与发布值不一致：期望 ' + $expected + '，实际 ' + $actual) }
    Write-Step 'OK' ('SHA-256 校验一致：' + $actual)
} else {
    Write-Step 'WARN' ('跳过校验，安装包 SHA-256：' + $actual)
}

# --- 2. destination ----------------------------------------------------------
if (-not $InstallRoot) { $InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\WeChatAiCopilot' }
if ($InstallRoot -notmatch 'WeChatAiCopilot') {
    throw '为了不误删其它目录，安装目录名必须包含 WeChatAiCopilot。'
}
Write-Step '...' ('安装目录：' + $InstallRoot)

# --- 3. refuse to overwrite a running application ----------------------------
$running = @()
foreach ($candidate in @('WeChatAiCopilot.Api', 'python', 'postgres')) {
    foreach ($process in (Get-Process -Name $candidate -ErrorAction SilentlyContinue)) {
        $path = ''
        try { $path = $process.Path } catch { $path = '' }
        if ($path -and $path.StartsWith($InstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) { $running += $path }
    }
}
if ($running.Count -gt 0) {
    throw ('目标目录里还有正在运行的进程，先双击「关闭正式项目」再安装：' + ($running -join '、'))
}

# --- 4. extract and verify before it becomes the installed copy --------------
$temp = Join-Path $env:TEMP ('copilot-install-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PackageZip, $temp)

    $manifestPath = Join-Path $temp 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw '安装包里没有 release-manifest.json。' }
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    if (-not $manifest.files) { throw 'release-manifest.json 里没有文件清单。' }

    $bad = @()
    foreach ($entry in $manifest.files) {
        $path = Join-Path $temp ($entry.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $bad += ($entry.path + '（缺失）'); continue }
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($hash -ne $entry.sha256.ToLowerInvariant()) { $bad += ($entry.path + '（哈希不一致）') }
    }
    if ($bad.Count -gt 0) {
        throw ('包内文件与清单不一致：' + (($bad | Select-Object -First 5) -join '、') +
            '（共 ' + $bad.Count + ' 项，包可能已损坏或被截断）')
    }
    Write-Step 'OK' ('清单核对通过：' + $manifest.files.Count + ' 个文件 · 版本 ' + $manifest.version)

    foreach ($required in @('启动正式项目.cmd', 'scripts\Start-FormalProject.ps1', 'scripts\Setup-TrialMachine.ps1', 'scripts\PostgresRuntime.ps1',
            'docs\operations\Agent_Install_Runbook.md',
            'src\WeChatAiCopilot.Reader\.runtime\package\UPSTREAM.txt',
            'src\WeChatAiCopilot.Reader\.runtime\package\wechatauto\db.py',
            'src\WeChatAiCopilot.Reader\.runtime\deps\cryptography\__init__.py',
            'src\WeChatAiCopilot.Reader\.runtime\deps\zstandard\__init__.py',
            'artifacts\app\win-x64\WeChatAiCopilot.Api.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $temp $required) -PathType Leaf)) {
            throw ('安装包里缺少必需文件：' + $required)
        }
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    $log = Join-Path $env:TEMP 'copilot-install-robocopy.log'
    $logSwitch = '/LOG:' + $log
    & robocopy $temp $InstallRoot '/MIR' '/NFL' '/NDL' '/NJH' '/NJS' '/R:2' '/W:1' $logSwitch | Out-Null
    $code = $LASTEXITCODE
    # robocopy: 0-7 means success; 8+ is a real failure.
    if ($code -ge 8) { throw ('复制文件失败，robocopy 代码 ' + $code + '，详见 ' + $log) }
    Write-Step 'OK' '文件已就位'

    $version = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $InstallRoot 'VERSION.txt')).Trim()
    Write-Host ''
    Write-Host ('知间已安装到：' + $InstallRoot + '  （版本 ' + $version + '）')
    Write-Host '下一步（同一台电脑只需一次）：准备数据库、只读组件与桌面快捷方式。'
    $setup = Join-Path $InstallRoot 'scripts\Setup-TrialMachine.ps1'
    if ($RunSetup) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $setup '-Yes'
        if ($LASTEXITCODE -ne 0) { throw '环境准备未通过，请看上面的 FAIL 行。' }
    } else {
        Write-Host ('  powershell -NoProfile -ExecutionPolicy Bypass -File "' + $setup + '" -Yes')
    }
    exit 0
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
