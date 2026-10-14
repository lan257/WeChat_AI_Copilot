# Backup and restore drill for the private formal database.
#
#   .\Backup-FormData.ps1                 take one verified backup
#   .\Backup-FormData.ps1 -Verify        also restore it into a scratch database and compare
#   .\Backup-FormData.ps1 -Keep 10       drop older dumps, keeping the newest N
#
# This never writes to the formal database. The restore drill uses a separate scratch database.
param(
    [switch]$Verify,
    [int]$Keep = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$productRoot = Split-Path -Parent $PSScriptRoot
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
$dataRoot = Join-Path $stateRoot 'postgres-data'
$backupDir = Join-Path $stateRoot 'backups'
$secretsPath = Join-Path $stateRoot 'secrets.json'
$databasePort = 55432
$databaseName = 'wechat_ai_copilot_formal'
$drillName = 'formal_restore_drill'

function Resolve-PostgresBin18 {
    $service = Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'" -ErrorAction SilentlyContinue
    if ($service -and $service.PathName -match '(?i)([A-Z]:\\.*?\\pg_ctl\.exe)') {
        $dir = Split-Path -Parent $Matches[1]
        if (Test-Path (Join-Path $dir 'pg_dump.exe')) { return $dir }
    }
    foreach ($dir in @('G:\PostgreSQL\18\bin', (Join-Path ${env:ProgramFiles} 'PostgreSQL\18\bin'))) {
        if (Test-Path (Join-Path $dir 'pg_dump.exe')) { return $dir }
    }
    throw '未找到 PostgreSQL 18 的 pg_dump，请确认数据库已安装。'
}

function Unprotect-LocalText([string] $value) {
    $secure = ConvertTo-SecureString -String $value
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

if (-not (Test-Path -LiteralPath $secretsPath)) { throw '正式项目还没有初始化过（缺少 secrets.json），无需备份。' }
$bin = Resolve-PostgresBin18
$pgCtl = Join-Path $bin 'pg_ctl.exe'
$psql = Join-Path $bin 'psql.exe'
$stored = Get-Content -Raw -LiteralPath $secretsPath | ConvertFrom-Json
$env:PGPASSWORD = Unprotect-LocalText ([string]$stored.bootstrapPassword)

# A backup of a stopped cluster is still valid, but a running one is the normal case; make sure the
# server is reachable before reporting anything as saved.
& $pgCtl '-D' $dataRoot 'status' *> $null
if ($LASTEXITCODE -ne 0) { throw '本机数据库未在运行，请先启动正式项目或使用 -Verify 前确认数据目录状态。' }

function Invoke-Query([string] $database, [string] $sql) {
    # Windows PowerShell 5.1 promotes native stderr to a terminating error, and psql writes ordinary
    # NOTICEs there, so run these with the preference relaxed and judge success by the exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $psql '-X' '-q' '-A' '-t' '-F' '|' '-v' 'ON_ERROR_STOP=1' '-h' '127.0.0.1' '-p' "$databasePort" '-U' 'copilot_bootstrap' '-d' $database '-c' $sql 2>$null
        if ($LASTEXITCODE -ne 0) { throw ('数据库语句执行失败：' + $sql) }
        return ($output -join ' ')
    } finally { $ErrorActionPreference = $previous }
}

$before = Invoke-Query $databaseName "SELECT 'tables='||(SELECT count(*) FROM information_schema.tables WHERE table_schema='copilot')||' migrations='||(SELECT count(*) FROM copilot.schema_migrations)||' messages='||(SELECT count(*) FROM copilot.chat_messages)||' conversations='||(SELECT count(*) FROM copilot.conversations)"
Write-Output ("正式库当前状态：" + $before)

if (-not (Test-Path -LiteralPath $backupDir)) { $null = New-Item -ItemType Directory -Force -Path $backupDir }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& (Join-Path $env:SystemRoot 'System32\icacls.exe') $backupDir '/inheritance:r' '/grant:r' ('*{0}:(OI)(CI)F' -f $sid) | Out-Null

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$dumpPath = Join-Path $backupDir ("formal-{0}.dump" -f $stamp)
& (Join-Path $bin 'pg_dump.exe') '-h' '127.0.0.1' '-p' "$databasePort" '-U' 'copilot_bootstrap' '-d' $databaseName '-Fc' '-f' $dumpPath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dumpPath)) { throw '备份失败：pg_dump 未生成文件。' }
$hash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash
$manifest = [ordered]@{ file = (Split-Path -Leaf $dumpPath); sha256 = $hash; bytes = (Get-Item -LiteralPath $dumpPath).Length; takenAtUtc = [DateTime]::UtcNow.ToString('o'); summary = $before }
[System.IO.File]::WriteAllText((Join-Path $backupDir ("formal-{0}.json" -f $stamp)), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output ("备份完成：" + $dumpPath + "（" + $manifest.bytes + " 字节）")
Write-Output ("SHA-256：" + $hash)

if ($Verify) {
    Write-Output '正在把备份恢复到独立的临时数据库做演练（不会改动正式库）…'
    $null = Invoke-Query 'postgres' "DROP DATABASE IF EXISTS $drillName"
    $null = Invoke-Query 'postgres' "CREATE DATABASE $drillName"
    $restoreLog = Join-Path $backupDir ("restore-{0}.log" -f $stamp)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & (Join-Path $bin 'pg_restore.exe') '-h' '127.0.0.1' '-p' "$databasePort" '-U' 'copilot_bootstrap' '-d' $drillName $dumpPath 2>&1 |
            Out-File -FilePath $restoreLog -Append -Encoding utf8
        $restoreExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    # pg_restore uses exit code 1 for non-fatal warnings, so the comparison below decides.
    if ($restoreExit -gt 1) { throw ('pg_restore 失败（退出码 ' + $restoreExit + '），详见 ' + $restoreLog) }
    $restored = Invoke-Query $drillName "SELECT 'tables='||(SELECT count(*) FROM information_schema.tables WHERE table_schema='copilot')||' migrations='||(SELECT count(*) FROM copilot.schema_migrations)||' messages='||(SELECT count(*) FROM copilot.chat_messages)||' conversations='||(SELECT count(*) FROM copilot.conversations)"
    $null = Invoke-Query 'postgres' "DROP DATABASE IF EXISTS $drillName"
    if ($restored -ne $before) {
        Write-Output ("恢复演练结果不一致！正式库：" + $before)
        Write-Output ("                     恢复库：" + $restored)
        throw '恢复演练未通过，请不要依赖这份备份。'
    }
    Write-Output ("恢复演练通过：" + $restored)
}

if ($Keep -gt 0) {
    $dumps = @(Get-ChildItem -LiteralPath $backupDir -Filter 'formal-*.dump' | Sort-Object LastWriteTime -Descending)
    if ($dumps.Count -gt $Keep) {
        foreach ($extra in $dumps[$Keep..($dumps.Count - 1)]) {
            $pair = [IO.Path]::ChangeExtension($extra.FullName, '.json')
            Remove-Item -LiteralPath $extra.FullName -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $pair) { Remove-Item -LiteralPath $pair -Force -ErrorAction SilentlyContinue }
            Write-Output ('已清理旧备份：' + $extra.Name)
        }
    }
}

Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
