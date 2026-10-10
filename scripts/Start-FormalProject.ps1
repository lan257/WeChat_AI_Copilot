$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$productRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $productRoot 'src\WeChatAiCopilot.Api'
$readerRoot = Join-Path $productRoot 'src\WeChatAiCopilot.Reader'
$readerScript = Join-Path $readerRoot 'reader.py'
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
$readerSettingsRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Reader'
$scopePath = Join-Path $stateRoot 'scope.json'
$secretsPath = Join-Path $stateRoot 'secrets.json'
$dataRoot = Join-Path $stateRoot 'postgres-data'
$databaseName = 'wechat_ai_copilot_formal'
$databaseUser = 'copilot_app'
$databasePort = 55432
$baseUrl = 'http://127.0.0.1:5080'
$historyMarker = Join-Path $stateRoot 'initial-history-imported.json'
$apiPidPath = Join-Path $stateRoot 'api.pid'
$apiLogPath = Join-Path $stateRoot 'api.log'
$apiErrorPath = Join-Path $stateRoot 'api-error.log'
$stagePath = Join-Path $stateRoot 'startup-stage.txt'

function Set-StartupStage([string] $name) {
    [System.IO.File]::WriteAllText($stagePath, $name, [System.Text.UTF8Encoding]::new($false))
    Write-Host $name
}

function Set-PrivateDirectoryAcl([string] $path) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $directoryAce = '*{0}:(OI)(CI)F' -f $sid
    $fileAce = '*{0}:F' -f $sid
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $path '/inheritance:r' '/grant:r' $directoryAce 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restrict local application data to the current Windows user.' }
    foreach ($file in Get-ChildItem -LiteralPath $path -File -Force -ErrorAction SilentlyContinue) {
        & $icacls $file.FullName '/grant:r' $fileAce 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict a local application data file to the current Windows user.' }
    }
}

function New-RandomHex([int] $byteCount = 32) {
    $bytes = [byte[]]::new($byteCount)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Protect-LocalText([string] $value) {
    $secure = ConvertTo-SecureString -String $value -AsPlainText -Force
    return ConvertFrom-SecureString -SecureString $secure
}

function Unprotect-LocalText([string] $value) {
    $secure = ConvertTo-SecureString -String $value
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Resolve-PostgresBin {
    $service = Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'" -ErrorAction SilentlyContinue
    if ($service -and $service.PathName -match '(?i)([A-Z]:\\[^" ]*?\\pg_ctl\.exe)') {
        $candidate = Split-Path -Parent $Matches[1]
        if (Test-Path (Join-Path $candidate 'initdb.exe')) { return $candidate }
    }
    foreach ($candidate in @('G:\PostgreSQL\18\bin', (Join-Path $env:ProgramFiles 'PostgreSQL\18\bin'))) {
        if (Test-Path (Join-Path $candidate 'initdb.exe')) { return $candidate }
    }
    throw 'PostgreSQL 18 is not installed. The one-click launcher cannot start the local database.'
}

function Read-LocalSecrets {
    if (-not (Test-Path -LiteralPath $secretsPath)) { return $null }
    $stored = Get-Content -Raw -LiteralPath $secretsPath | ConvertFrom-Json
    return [pscustomobject]@{
        DatabasePassword = Unprotect-LocalText ([string]$stored.databasePassword)
        BootstrapPassword = Unprotect-LocalText ([string]$stored.bootstrapPassword)
        WebToken = Unprotect-LocalText ([string]$stored.webToken)
        ReaderToken = Unprotect-LocalText ([string]$stored.readerToken)
    }
}

function Save-LocalSecrets($secrets) {
    $stored = [ordered]@{
        databasePassword = Protect-LocalText $secrets.DatabasePassword
        bootstrapPassword = Protect-LocalText $secrets.BootstrapPassword
        webToken = Protect-LocalText $secrets.WebToken
        readerToken = Protect-LocalText $secrets.ReaderToken
    }
    $temporaryPath = Join-Path $stateRoot 'secrets.tmp'
    [System.IO.File]::WriteAllText($temporaryPath, ($stored | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $secretsPath -Force
}

function Invoke-LocalPsql([string] $psqlPath, $secrets, [string] $database, [string] $scriptPath) {
    $env:PGPASSWORD = $secrets.BootstrapPassword
    try {
        & $psqlPath '-X' '-q' '-v' 'ON_ERROR_STOP=1' '-h' '127.0.0.1' '-p' "$databasePort" '-U' 'copilot_bootstrap' '-d' $database '-f' $scriptPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The local database could not be prepared.' }
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Ensure-LocalDatabase([string] $bin, $secrets) {
    $pgCtl = Join-Path $bin 'pg_ctl.exe'
    $initDb = Join-Path $bin 'initdb.exe'
    $psql = Join-Path $bin 'psql.exe'
    $postgres = Join-Path $bin 'postgres.exe'
    if (-not (Test-Path (Join-Path $dataRoot 'PG_VERSION'))) {
        Set-StartupStage '正在建立本机专用数据库…'
        if (Test-Path $dataRoot) {
            $existingItems = @(Get-ChildItem -LiteralPath $dataRoot -Force -ErrorAction SilentlyContinue)
            if ($existingItems.Count -gt 0) { throw 'The private application database folder is incomplete; it was left untouched.' }
        }
        $passwordFile = Join-Path $stateRoot 'bootstrap-password.tmp'
        [System.IO.File]::WriteAllText($passwordFile, $secrets.BootstrapPassword + "`n", [System.Text.UTF8Encoding]::new($false))
        try {
            & $initDb '-D' $dataRoot '-U' 'copilot_bootstrap' '--encoding=UTF8' '--auth-local=scram-sha-256' '--auth-host=scram-sha-256' "--pwfile=$passwordFile" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'The private local database could not be initialized.' }
        }
        finally { Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue }
    }

    & $pgCtl '-D' $dataRoot 'status' *> $null
    if ($LASTEXITCODE -ne 0) {
        Set-StartupStage '正在启动本机专用数据库…'
        $postgresProcess = Start-Process -FilePath $postgres -ArgumentList @('-D', $dataRoot, '-h', '127.0.0.1', '-p', "$databasePort") -WorkingDirectory $stateRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $stateRoot 'postgres.log') -RedirectStandardError (Join-Path $stateRoot 'postgres-error.log')
        Set-StartupStage '本机数据库进程已启动，正在等待就绪…'

        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        $ready = $false
        do {
            $env:PGPASSWORD = $secrets.BootstrapPassword
            $env:PGCONNECT_TIMEOUT = '2'
            try {
                $probe = & $psql '-X' '-q' '-A' '-t' '-h' '127.0.0.1' '-p' "$databasePort" '-U' 'copilot_bootstrap' '-d' 'postgres' '-c' 'SELECT 1' 2>$null
                $ready = ($LASTEXITCODE -eq 0 -and ($probe -join '').Trim() -eq '1')
            }
            finally {
                Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
                Remove-Item Env:PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue
            }
            if (-not $ready) { Start-Sleep -Milliseconds 750 }
            $postgresProcess.Refresh()
            if ($postgresProcess.HasExited -and -not $ready) { break }
        } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
        if (-not $ready) { throw 'The private local database did not become ready.' }
        Set-StartupStage '本机数据库已就绪。'
    }

    $sqlPath = Join-Path $stateRoot 'prepare-database.tmp.sql'
    $sql = @"
SELECT format('CREATE ROLE copilot_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD %L', '$($secrets.DatabasePassword)')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'copilot_app') \gexec
ALTER ROLE copilot_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '$($secrets.DatabasePassword)';
SELECT format('CREATE DATABASE wechat_ai_copilot_formal OWNER copilot_app')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'wechat_ai_copilot_formal') \gexec
"@
    [System.IO.File]::WriteAllText($sqlPath, $sql, [System.Text.UTF8Encoding]::new($false))
    Set-StartupStage '正在配置本机数据库访问…'
    try { Invoke-LocalPsql $psql $secrets 'postgres' $sqlPath }
    finally { Remove-Item -LiteralPath $sqlPath -Force -ErrorAction SilentlyContinue }
    Set-StartupStage '本机数据库访问已配置。'

    $connectionTest = Join-Path $stateRoot 'database-ready.tmp.sql'
    [System.IO.File]::WriteAllText($connectionTest, 'SELECT 1;', [System.Text.UTF8Encoding]::new($false))
    $env:PGPASSWORD = $secrets.DatabasePassword
    try {
        & $psql '-X' '-q' '-v' 'ON_ERROR_STOP=1' '-h' '127.0.0.1' '-p' "$databasePort" '-U' $databaseUser '-d' $databaseName '-f' $connectionTest | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The private local application database is not ready.' }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $connectionTest -Force -ErrorAction SilentlyContinue
    }
    Set-StartupStage '本机应用数据库已就绪。'
}

function Write-ReaderSettings($scope, $secrets) {
    Set-PrivateDirectoryAcl $readerSettingsRoot
    $settings = [ordered]@{
        schemaVersion = 1
        enabled = $true
        accountExternalId = [string]$scope.accountExternalId
        conversationExternalId = [string]$scope.conversationExternalId
        transport = [ordered]@{
            enabled = $true
            apiBaseUrl = $baseUrl
            readerBearerToken = $secrets.ReaderToken
        }
    }
    $settingsPath = Join-Path $readerSettingsRoot 'settings.json'
    [System.IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
}

function Start-FormalApi($scope, $secrets) {
    $existingProcessId = 0
    if (Test-Path -LiteralPath $apiPidPath) {
        $candidateId = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $apiPidPath), [ref]$candidateId)) {
            $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$candidateId" -ErrorAction SilentlyContinue
            if ($candidate -and $candidate.CommandLine -like '*WeChatAiCopilot.Api.dll*') {
                $existingProcessId = $candidateId
            }
        }
    }
    if ($existingProcessId -gt 0) {
        try {
            $ready = Invoke-WebRequest -Uri "$baseUrl/health/ready" -UseBasicParsing -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { return }
        }
        catch { throw 'A project API process is recorded, but it is not ready. Use the stop window before starting it again.' }
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5080)
    try { $listener.Start(); $listener.Stop() }
    catch { throw 'The local Web address is already in use. No new listener was opened.' }

    $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { throw '.NET 10 is not installed. The one-click launcher cannot start the formal app.' }
    $buildLog = Join-Path $stateRoot 'build.log'
    & $dotnet 'build' (Join-Path $productRoot 'WeChatAiCopilot.sln') '-c' 'Release' '--nologo' '-v:q' *> $buildLog
    if ($LASTEXITCODE -ne 0) { throw 'The formal app could not be built. See the local build log under the application data folder.' }
    $apiDll = Join-Path $apiProject 'bin\Release\net10.0\WeChatAiCopilot.Api.dll'
    if (-not (Test-Path -LiteralPath $apiDll)) { throw 'The formal app build did not produce the expected API file.' }

    $env:ASPNETCORE_URLS = $baseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:ConnectionStrings__FormalDatabase = "Host=127.0.0.1;Port=$databasePort;Database=$databaseName;Username=$databaseUser;Password=$($secrets.DatabasePassword);Timeout=15;Command Timeout=60"
    $env:Authentication__WebBearerToken = $secrets.WebToken
    $env:Authentication__ReaderBearerToken = $secrets.ReaderToken
    $env:Web__AllowedAccountExternalId = [string]$scope.accountExternalId
    $env:Web__AllowedConversationExternalId = [string]$scope.conversationExternalId
    $env:Reader__AllowedAccountExternalId = [string]$scope.accountExternalId
    $env:Reader__AllowedConversationExternalId = [string]$scope.conversationExternalId
    $env:Agent__Runtime = 'CodexCli'
    $env:Agent__SkillVersion = 'reply-v1'
    $env:Agent__RecentMessageLimit = '30'
    $env:Agent__IncludeMemory = 'true'
    $env:Agent__Budget__MaxDurationSeconds = '180'
    $env:Agent__Budget__MaxContextCharacters = '20000'
    $env:Agent__Budget__MaxToolCalls = '8'
    $env:Agent__AllowedPersonaPresetIds__0 = 'natural'
    $env:Agent__AllowedPersonaPresetIds__1 = 'warm'
    $env:Agent__AllowedPersonaPresetIds__2 = 'playful'
    $env:Agent__AllowedPersonaPresetIds__3 = 'concise'
    $env:Agent__AllowedReadCapabilities__0 = 'GetRecentMessages'
    $env:Agent__AllowedReadCapabilities__1 = 'SearchChatHistory'
    $env:Agent__AllowedReadCapabilities__2 = 'GetContactMemory'
    $env:Agent__AllowedReadCapabilities__3 = 'GetMessageEvidence'
    try {
        $process = Start-Process -FilePath $dotnet -ArgumentList @($apiDll) -WorkingDirectory $apiProject -WindowStyle Hidden -PassThru -RedirectStandardOutput $apiLogPath -RedirectStandardError $apiErrorPath
        Set-Content -LiteralPath $apiPidPath -Value $process.Id -Encoding ascii
    }
    finally {
        @('ASPNETCORE_URLS','ASPNETCORE_ENVIRONMENT','ConnectionStrings__FormalDatabase','Authentication__WebBearerToken','Authentication__ReaderBearerToken','Web__AllowedAccountExternalId','Web__AllowedConversationExternalId','Reader__AllowedAccountExternalId','Reader__AllowedConversationExternalId','Agent__Runtime','Agent__SkillVersion','Agent__RecentMessageLimit','Agent__IncludeMemory','Agent__Budget__MaxDurationSeconds','Agent__Budget__MaxContextCharacters','Agent__Budget__MaxToolCalls','Agent__AllowedPersonaPresetIds__0','Agent__AllowedPersonaPresetIds__1','Agent__AllowedPersonaPresetIds__2','Agent__AllowedPersonaPresetIds__3','Agent__AllowedReadCapabilities__0','Agent__AllowedReadCapabilities__1','Agent__AllowedReadCapabilities__2','Agent__AllowedReadCapabilities__3') | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $ready = Invoke-WebRequest -Uri "$baseUrl/health/ready" -UseBasicParsing -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Milliseconds 750 }
        $process.Refresh()
        if ($process.HasExited) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The formal app did not become ready. The local API log contains only service diagnostics.'
}

function Import-InitialHistory($scope, $secrets, [string] $pythonExe) {
    if (Test-Path -LiteralPath $historyMarker) { return }
    if (-not (Test-Path -LiteralPath $readerScript)) { throw 'The formal Reader source is missing.' }

    $readerSettings = Join-Path $readerSettingsRoot 'settings.json'
    Write-ReaderSettings $scope $secrets
    $historyLog = Join-Path $stateRoot 'reader-history.log'
    $jobId = 'local-first-' + [Guid]::NewGuid().ToString('N')
    & $pythonExe $readerScript 'history' '--job-id' $jobId '--max-rows' '5000' '--batch-size' '100' *> $historyLog
    if ($LASTEXITCODE -ne 0) { throw 'The real WeChat history import failed; no synthetic messages were used. See the sanitized Reader status log.' }

    $psql = Join-Path (Resolve-PostgresBin) 'psql.exe'
    $accountSql = ([string]$scope.accountExternalId).Replace("'", "''")
    $conversationSql = ([string]$scope.conversationExternalId).Replace("'", "''")
    $displayNameSql = ([string]$scope.displayName).Replace("'", "''")
    $metadataSqlPath = Join-Path $stateRoot 'set-contact-name.tmp.sql'
    $metadataSql = "UPDATE copilot.contacts AS contact SET display_name = '$displayNameSql' FROM copilot.chat_accounts AS account WHERE contact.account_id = account.id AND account.provider = 'local-wechat' AND account.external_account_id = '$accountSql' AND contact.external_contact_id = '$conversationSql';`n"
    [System.IO.File]::WriteAllText($metadataSqlPath, $metadataSql, [System.Text.UTF8Encoding]::new($false))
    $env:PGPASSWORD = $secrets.DatabasePassword
    try {
        & $psql '-X' '-q' '-v' 'ON_ERROR_STOP=1' '-h' '127.0.0.1' '-p' "$databasePort" '-U' $databaseUser '-d' $databaseName '-f' $metadataSqlPath *> $null
        if ($LASTEXITCODE -ne 0) { throw 'The selected contact label could not be saved.' }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $metadataSqlPath -Force -ErrorAction SilentlyContinue
    }

    $request = [Net.HttpWebRequest]::Create("$baseUrl/api/conversations/current")
    $request.Headers['Authorization'] = "Bearer $($secrets.WebToken)"
    try {
        $response = $request.GetResponse()
        try {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            try { $conversation = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }
        finally { $response.Dispose() }
    }
    catch { throw 'History import completed, but the formal Web workspace could not verify the selected conversation.' }
    if ($conversation.displayName -ne $scope.displayName) { throw 'The Web workspace did not return the selected contact label.' }
    [System.IO.File]::WriteAllText($historyMarker, (ConvertTo-Json @{ importedAtUtc = [DateTime]::UtcNow.ToString('o'); jobId = $jobId }), [System.Text.UTF8Encoding]::new($false))
}

function Stop-FormalServices([string] $bin) {
    if (Test-Path -LiteralPath $apiPidPath) {
        $candidateId = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $apiPidPath), [ref]$candidateId)) {
            $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$candidateId" -ErrorAction SilentlyContinue
            if ($candidate -and $candidate.CommandLine -like '*WeChatAiCopilot.Api.dll*') {
                Stop-Process -Id $candidateId -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item -LiteralPath $apiPidPath -Force -ErrorAction SilentlyContinue
    }
    $pgCtl = Join-Path $bin 'pg_ctl.exe'
    & $pgCtl '-D' $dataRoot '-m' 'fast' '-w' 'stop' *> $null
}

$bin = $null
try {
    Set-PrivateDirectoryAcl $stateRoot
    Set-PrivateDirectoryAcl $readerSettingsRoot
    Set-StartupStage '正在读取已授权会话配置…'
    if (-not (Test-Path -LiteralPath $scopePath)) { throw 'No approved real WeChat contact is configured for this Windows user.' }
    $scope = Get-Content -Raw -LiteralPath $scopePath | ConvertFrom-Json
    if ($scope.provider -ne 'local-wechat' -or -not $scope.accountExternalId -or -not $scope.conversationExternalId -or -not $scope.displayName) {
        throw 'The selected WeChat conversation configuration is incomplete.'
    }
    if ([string]$scope.conversationExternalId -match '(@chatroom$|^gh_)') { throw 'Only a direct one-to-one conversation is supported.' }

    $secrets = Read-LocalSecrets
    if (-not $secrets) {
        $secrets = [pscustomobject]@{
            DatabasePassword = New-RandomHex
            BootstrapPassword = New-RandomHex
            WebToken = New-RandomHex 48
            ReaderToken = New-RandomHex 48
        }
        Save-LocalSecrets $secrets
    }

    $bin = Resolve-PostgresBin
    Set-StartupStage '正在准备本机数据库…'
    Ensure-LocalDatabase $bin $secrets

    Set-StartupStage '正在检查 Python 3.11 和 Reader…'
    $pythonExe = (& py -3.11 -c 'import sys; print(sys.executable)').Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pythonExe)) { throw 'Python 3.11 is not available for the read-only WeChat Reader.' }
    Write-Host '正在验证已准备的微信只读组件…'
    $readerRuntime = Join-Path $readerRoot '.runtime\package\wechatauto\db.py'
    if (-not (Test-Path -LiteralPath $readerRuntime)) {
        throw 'The verified Reader runtime has not been prepared. Contact support before continuing.'
    }

    $wechatProcess = Get-Process -Name 'Weixin' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $wechatProcess) {
        $wechatPath = [string]$scope.wechatExecutablePath
        if (-not $wechatPath -or -not (Test-Path -LiteralPath $wechatPath)) { throw 'Open and sign in to the WeChat desktop app, then start the formal project again.' }
        Start-Process -FilePath $wechatPath
        Start-Sleep -Seconds 4
    }

    Write-Host '本机数据库已就绪。正在构建并启动正式工作区…'
    Write-ReaderSettings $scope $secrets
    Start-FormalApi $scope $secrets
    Import-InitialHistory $scope $secrets $pythonExe

    Set-Clipboard -Value $secrets.WebToken
    Start-Process $baseUrl
    Write-Host ''
    Write-Host '正式工作区已启动，浏览器已打开。Web 令牌已复制到剪贴板，请在网页中粘贴并点击“连接并读取”。'
    Write-Host '只读采集正在运行。按 Ctrl+C 可停止 Reader；随后 API 和本地数据库会一并关闭。'
    Write-Host 'Reader 仅显示状态回执，不会在此窗口输出微信消息正文。'
    Write-Host ''

    & $pythonExe $readerScript 'realtime'
    if ($LASTEXITCODE -ne 0) { Write-Host 'Reader 已停止。请查看本机应用状态日志中的错误码。' }
}
catch {
    Write-Host ''
    Write-Host ('启动未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($bin) { Stop-FormalServices $bin }
}
