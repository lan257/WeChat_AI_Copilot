# Build a shippable, verifiable Windows package for 知间 (WeChat AI Copilot).
#
# The package keeps the repository layout, so the launcher and setup script run from the installed
# folder without a second code path. Recipients also receive Install-FromPackage.ps1 and the
# self-contained Agent_Install_Runbook.md; the product's Codex CLI login remains user-owned.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-ReleasePackage.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-ReleasePackage.ps1 -Version 0.8.2
#
# Nothing here signs the package. An unsigned build still triggers SmartScreen on a fresh machine;
# see docs/operations/Release_and_Upgrade.md for what is and is not promised.
param(
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell 5.1 decodes a BOM-less file as the ANSI codepage, which mangles every Chinese
# string in this script and breaks argument quoting.
if ((Get-Variable -Name PSVersionTable -ValueOnly).PSVersion.Major -lt 6) {
    $scriptBytes = [System.IO.File]::ReadAllBytes($PSCommandPath)
    if ($scriptBytes.Length -lt 3 -or $scriptBytes[0] -ne 0xEF -or $scriptBytes[1] -ne 0xBB -or $scriptBytes[2] -ne 0xBF) {
        throw '这个脚本必须保存为「带有 BOM 的 UTF-8」，否则 Windows PowerShell 5.1 会把中文当成本地编码来解析。'
    }
}

$productRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $productRoot 'src\WeChatAiCopilot.Api'
$readerRoot = Join-Path $productRoot 'src\WeChatAiCopilot.Reader'
$agentRunbook = Join-Path $productRoot 'docs\operations\Agent_Install_Runbook.md'
$requiredReaderVersion = '1.2.7.0'
$releaseRoot = if ($OutDir) { $OutDir } else { Join-Path $productRoot 'artifacts\release' }
$dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source

function Write-Step([string]$state, [string]$message) {
    Write-Host ('[{0}] {1}' -f $state, $message)
}

function Get-TextHash([string]$value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Get-BuildIdentity([string]$root) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm')
    $git = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
    $commit = 'nogit'
    if ($git) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        $reported = & $git -C $root rev-parse --short HEAD 2>$null
        $ErrorActionPreference = $previous
        if ($reported) { $commit = ($reported | Select-Object -First 1).Trim() }
    }
    return '0.0.0-{0}+{1}' -f $stamp, $commit
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

if (-not $dotnet) { throw '找不到 dotnet，无法生成发布包。' }
if (-not (Test-Path -LiteralPath $agentRunbook -PathType Leaf)) { throw '缺少 docs\operations\Agent_Install_Runbook.md，不能生成新环境交付包。' }
$buildVersion = if ($Version) { $Version } else { Get-BuildIdentity $productRoot }
$packageName = 'WeChatAiCopilot-{0}-win-x64' -f ($buildVersion -replace '[^\w.\-]', '-')
$staging = Join-Path $env:TEMP ('copilot-release-' + [Guid]::NewGuid().ToString('N'))
$appStage = Join-Path $staging 'artifacts\app\win-x64'

try {
    New-Item -ItemType Directory -Force -Path $appStage | Out-Null
    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

    # A recipient should not need PyPI access to prepare the pinned read-only runtime.
    if (-not (Test-ReaderRuntime)) {
        Write-Step 'RUN' ('准备随包交付的 Reader runtime ' + $requiredReaderVersion)
        $prepareReader = Join-Path $readerRoot 'prepare-reader.ps1'
        if (-not (Test-Path -LiteralPath $prepareReader -PathType Leaf)) { throw '缺少 Reader prepare-reader.ps1。' }
        & (Get-Command powershell.exe -ErrorAction Stop).Source -NoProfile -ExecutionPolicy Bypass -File $prepareReader
        if ($LASTEXITCODE -ne 0 -or -not (Test-ReaderRuntime)) {
            throw 'Reader runtime 未能准备到固定版本；请先安装 64 位 Python 3.11 并确认构建机可访问 PyPI。'
        }
        Write-Step 'OK' ('Reader runtime ' + $requiredReaderVersion + ' 已准备')
    }

    # --- 1. self-contained application ---------------------------------------
    if ($SkipPublish) {
        $built = Join-Path $productRoot 'artifacts\app\win-x64'
        if (-not (Test-Path -LiteralPath (Join-Path $built 'WeChatAiCopilot.Api.exe') -PathType Leaf)) {
            throw '-SkipPublish 要求 artifacts\app\win-x64 已有生成好的应用。'
        }
        # Shipping yesterday's binary is the most dangerous way to save two minutes, so compare it
        # against the newest source instead of trusting that the folder exists.
        $binary = Get-Item -LiteralPath (Join-Path $built 'WeChatAiCopilot.Api.dll')
        $newestSource = Get-ChildItem -LiteralPath $productRoot -File -Recurse -Include '*.cs', '*.cshtml', '*.html', '*.js', '*.css', '*.sql', 'reader.py' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(artifacts|bin|obj|node_modules|\.git)\\' } |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($newestSource -and $newestSource.LastWriteTimeUtc -gt $binary.LastWriteTimeUtc) {
            throw ('artifacts\app\win-x64 比源码旧（源码更新：' + $newestSource.Name + ' ' + $newestSource.LastWriteTime.ToString('yyyy-MM-dd HH:mm') +
                '，应用生成于 ' + $binary.LastWriteTime.ToString('yyyy-MM-dd HH:mm') + '）。去掉 -SkipPublish 重新生成。')
        }
        Copy-Item -Path (Join-Path $built '*') -Destination $appStage -Recurse -Force
        if (-not (Test-Path -LiteralPath (Join-Path $appStage 'WeChatAiCopilot.Api.exe') -PathType Leaf)) {
            throw '复制已生成的应用失败，artifacts\app\win-x64 里没有可执行入口。'
        }
        Write-Step 'OK' ('复用已生成的应用：' + $built)
    } else {
        Write-Step '...' ('生成自包含应用：' + $buildVersion)
        & $dotnet 'publish' $apiProject '-c' 'Release' '-r' 'win-x64' '--self-contained' 'true' `
            '-o' $appStage '--nologo' '-v' 'q' 2>&1 | ForEach-Object { "$_" }
        if (-not (Test-Path -LiteralPath (Join-Path $appStage 'WeChatAiCopilot.Api.exe') -PathType Leaf)) {
            throw '自包含应用生成失败。'
        }
        Write-Step 'OK' '应用本体已生成（目标机不需要安装 .NET）'
        # Keep the local build cache identical to the shipped copy, so a later -SkipPublish or the
        # launcher cannot pick up an older binary by accident.
        $cache = Join-Path $productRoot 'artifacts\app\win-x64'
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        Copy-Item -Path (Join-Path $appStage '*') -Destination $cache -Recurse -Force
    }

    # --- 2. runtime sources the launcher needs -------------------------------
    foreach ($item in @('scripts', 'db\migrations', 'src\WeChatAiCopilot.Reader')) {
        $source = Join-Path $productRoot $item
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw ('发布包缺少 ' + $item) }
        $target = Join-Path $staging $item
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }
    foreach ($entry in (Get-ChildItem -LiteralPath (Join-Path $staging 'src\WeChatAiCopilot.Reader') -Directory -Filter '__pycache__' -Recurse)) {
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
    }
    foreach ($launcher in @('启动正式项目.cmd', '关闭正式项目.cmd', 'Copy-WebToken.cmd')) {
        $path = Join-Path $productRoot $launcher
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('发布包缺少 ' + $launcher) }
        Copy-Item -LiteralPath $path -Destination $staging -Force
    }
    $runbookTarget = Join-Path $staging 'docs\operations\Agent_Install_Runbook.md'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $runbookTarget) | Out-Null
    Copy-Item -LiteralPath $agentRunbook -Destination $runbookTarget -Force
    Write-Step 'OK' '启动脚本、迁移、固定版只读 runtime 与 Agent 安装手册已加入'

    # --- 3. manifest ---------------------------------------------------------
    $files = Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName
    $entries = @()
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($staging.Length + 1).Replace('\', '/')
        $entries += [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            bytes = $file.Length
        }
    }
    $manifest = [ordered]@{
        product = 'WeChat AI Copilot (知间)'
        version = $buildVersion
        builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        runtime = 'win-x64 self-contained'
        signed = $false
        entryPoint = '启动正式项目.cmd'
        fileCount = $entries.Count
        files = $entries
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $staging 'release-manifest.json'), $manifestJson, [System.Text.UTF8Encoding]::new($false))
    $sums = ($entries | ForEach-Object { '{0}  {1}' -f $_.sha256, $_.path }) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText((Join-Path $staging 'SHA256SUMS.txt'), ($sums + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $staging 'VERSION.txt'), ($buildVersion + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    Write-Step 'OK' ('清单包含 ' + $entries.Count + ' 个文件')

    # --- 4. zip and its own checksum ----------------------------------------
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $releaseRoot ($packageName + '.zip')
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(($zipPath + '.sha256'), ($zipHash + '  ' + $packageName + '.zip' + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))

    # --- 5. verify the produced archive, not just the staging copy ----------
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        # .NET Framework stores archive entry names with backslashes, so compare normalized paths.
        $byName = @{}
        foreach ($entry in $archive.Entries) { $byName[$entry.FullName.Replace('\', '/')] = $entry }
        $required = @('启动正式项目.cmd', 'scripts/Start-FormalProject.ps1', 'scripts/Setup-TrialMachine.ps1',
            'scripts/Install-FromPackage.ps1', 'src/WeChatAiCopilot.Reader/reader.py',
            'src/WeChatAiCopilot.Reader/.runtime/package/UPSTREAM.txt',
            'src/WeChatAiCopilot.Reader/.runtime/package/wechatauto/db.py',
            'src/WeChatAiCopilot.Reader/.runtime/deps/cryptography/__init__.py',
            'src/WeChatAiCopilot.Reader/.runtime/deps/zstandard/__init__.py',
            'docs/operations/Agent_Install_Runbook.md',
            'artifacts/app/win-x64/WeChatAiCopilot.Api.exe', 'release-manifest.json', 'SHA256SUMS.txt')
        $missing = @($required | Where-Object { -not $byName.ContainsKey($_) })
        if ($missing.Count -gt 0) { throw ('压缩包缺少必需文件：' + ($missing -join '、')) }
        $entry = $byName['artifacts/app/win-x64/WeChatAiCopilot.Api.dll']
        if (-not $entry) { throw '压缩包里没有应用主程序集。' }
        $rootPage = $byName['artifacts/app/win-x64/wwwroot/index.html']
        if (-not $rootPage) { throw '发布包里没有入口页，装完打不开工作区。' }
        $reader = New-Object System.IO.StreamReader($rootPage.Open())
        try { $html = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($html -notmatch '/v8/main\.js') { throw '入口页不是 V8 工作区，发布包内容有误。' }
    } finally { $archive.Dispose() }
    Write-Step 'OK' ('压缩包自检通过：' + $zipPath)

    # The recipient only has the zip, so the installer has to sit next to it, not inside it.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-FromPackage.ps1') -Destination $releaseRoot -Force
    Copy-Item -LiteralPath $agentRunbook -Destination $releaseRoot -Force
    $releaseNote = @(
        '知间 · Windows 发布包',
        ('版本：' + $buildVersion),
        ('安装包：' + (Split-Path -Leaf $zipPath)),
        ('SHA-256：' + $zipHash),
        '',
        '收到四样文件（安装包、.sha256、Install-FromPackage.ps1、Agent_Install_Runbook.md）后，全部放在同一个目录。',
        '把 Agent_Install_Runbook.md 和同目录的三个安装文件一起提供给执行部署的 Agent；在同目录执行：',
        ('  powershell -NoProfile -ExecutionPolicy Bypass -File Install-FromPackage.ps1 -PackageZip "' + (Split-Path -Leaf $zipPath) + '" -RunSetup'),
        '',
        '脚本会校验哈希、解压到 %LOCALAPPDATA%\Programs\WeChatAiCopilot，再自动准备数据库、只读组件与桌面快捷方式。',
        '安装包已内含固定版 Reader runtime 和 Agent 安装手册；目标电脑仍需 Python 3.11、PostgreSQL 18。',
        '之后日常只需双击桌面上的「启动正式项目」。软件只读微信，不会代你发送消息。',
        '未签名安装包在陌生电脑上会先出现 SmartScreen 提示，详见 docs\operations\Release_and_Upgrade.md。'
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'RELEASE.txt'), ($releaseNote + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($true))
    Write-Step 'OK' ('交付说明已写入 ' + (Join-Path $releaseRoot 'RELEASE.txt'))

    Write-Host ''
    Write-Host ('版本：' + $buildVersion)
    Write-Host ('文件数：' + $entries.Count)
    Write-Host ('大小：{0:N1} MB' -f ((Get-Item -LiteralPath $zipPath).Length / 1MB))
    Write-Host ('SHA-256：' + $zipHash)
    Write-Host ''
    Write-Host ('把四样一起交给对方（安装包、.sha256、Install-FromPackage.ps1、Agent_Install_Runbook.md），都在：' + $releaseRoot)
    Write-Host ('对方执行：powershell -NoProfile -ExecutionPolicy Bypass -File Install-FromPackage.ps1 -PackageZip "' + (Split-Path -Leaf $zipPath) + '" -RunSetup')
    exit 0
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
