$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$productRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $productRoot 'src\WeChatAiCopilot.Api'
$readerRoot = Join-Path $productRoot 'src\WeChatAiCopilot.Reader'
$readerScript = Join-Path $readerRoot 'reader.py'
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
$readerSettingsRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Reader'
$scopePath = Join-Path $stateRoot 'scope.json'
$approvedScopePath = Join-Path $stateRoot 'scope-approved.sha256'
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
$readerPidPath = Join-Path $stateRoot 'reader.pid'
$readerLogPath = Join-Path $stateRoot 'reader.log'
$readerErrorPath = Join-Path $stateRoot 'reader-error.log'
$stopOnExit = $true
$script:apiStartedByLauncher = $false
$script:databaseStartedByLauncher = $false
$script:readerStartedByLauncher = $false

function Open-AuthenticatedWorkspace {
    $grant = New-RandomHex
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $grantHash = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($grant))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $bootstrap = [ordered]@{ tokenHash = $grantHash; expiresAtUtc = [DateTime]::UtcNow.AddMinutes(2).ToString('o') }
    $bootstrapPath = Join-Path $stateRoot 'web-bootstrap.json'
    [System.IO.File]::WriteAllText($bootstrapPath, ($bootstrap | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
    Start-Process ($baseUrl + '/#bootstrap=' + $grant)
}

function Start-ManagedReader([string] $pythonExe) {
    if (Test-Path -LiteralPath $readerPidPath) {
        $readerCandidateId = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $readerPidPath), [ref]$readerCandidateId)) {
            $readerCandidate = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $readerCandidateId) -ErrorAction SilentlyContinue
            if ($readerCandidate -and $readerCandidate.ExecutablePath -eq $pythonExe -and
                $readerCandidate.CommandLine -like ('*' + $readerScript + '*') -and $readerCandidate.CommandLine -match '\brealtime\b') { return }
        }
    }
    $readerArguments = '"' + $readerScript + '" realtime'
    $managedReader = Start-Process -FilePath $pythonExe -ArgumentList $readerArguments -WorkingDirectory $readerRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $readerLogPath -RedirectStandardError $readerErrorPath
    $script:readerStartedByLauncher = $true
    Set-Content -LiteralPath $readerPidPath -Value $managedReader.Id -Encoding ascii
    Start-Sleep -Milliseconds 500
    $managedReader.Refresh()
    if ($managedReader.HasExited) { throw '本机读取器未能启动，请在运行状态中检查读取器故障；真实聊天正文不会写入日志。' }
}

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

function Test-ReaderPython311([string] $pythonPath) {
    if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) { return $false }
    try {
        & $pythonPath '-I' '-c' 'import sys; sys.exit(0 if sys.version_info[:2] == (3, 11) else 1)' *> $null
        return $LASTEXITCODE -eq 0
    }
    catch { return $false }
}

function Resolve-ReaderPython311 {
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        try {
            $output = @(& $launcher.Source '-3.11' '-c' 'import sys; print(sys.executable)' 2>$null)
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0 -and $output.Count -gt 0) {
                $candidate = ([string]$output[-1]).Trim()
                if (Test-ReaderPython311 $candidate) { return $candidate }
            }
        }
        catch { }
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'),
        (Join-Path $env:ProgramFiles 'Python311\python.exe'),
        'C:\Python311\python.exe'
    ) | Select-Object -Unique
    foreach ($candidate in $candidates) {
        if (Test-ReaderPython311 $candidate) { return $candidate }
    }

    throw '未找到可运行已准备 Reader 组件的 Python 3.11。请确认已为当前 Windows 用户安装 Python 3.11 后再次启动；完成此项检查前不会初始化本机数据库。'
}

function Get-ScopeFingerprint($scope) {
    $identity = @(
        [string]$scope.provider,
        [string]$scope.accountExternalId,
        [string]$scope.conversationExternalId,
        [string]$scope.displayName
    ) -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Test-ValidLocalScope($scope) {
    if (-not $scope -or
        $scope.provider -ne 'local-wechat' -or
        [string]::IsNullOrWhiteSpace([string]$scope.accountExternalId) -or
        [string]::IsNullOrWhiteSpace([string]$scope.conversationExternalId) -or
        [string]::IsNullOrWhiteSpace([string]$scope.displayName)) { return $false }
    if ([string]$scope.conversationExternalId -match '(@chatroom$|^gh_)') { return $false }
    return $true
}

function Save-LocalScopeApproval($scope) {
    $fingerprint = Get-ScopeFingerprint $scope
    [System.IO.File]::WriteAllText($approvedScopePath, $fingerprint, [System.Text.UTF8Encoding]::new($false))
}

function Resolve-LocalContactScope([string] $pythonExe, [string] $displayName, [string] $wechatExecutablePath) {
    $requestPath = Join-Path $stateRoot 'scope-request.tmp.json'
    $resolvedPath = Join-Path $stateRoot 'scope.tmp.json'
    $request = [ordered]@{ displayName = $displayName; wechatExecutablePath = $wechatExecutablePath }
    [System.IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
    try {
        & $pythonExe $readerScript 'resolve-scope' '--request-file' $requestPath '--output' $resolvedPath *> $null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            throw '无法在本机微信中唯一确认该显示名对应的一对一联系人；未保存会话授权配置。请确认微信已登录且该显示名唯一。'
        }

        $resolvedJson = [System.IO.File]::ReadAllText($resolvedPath, [System.Text.Encoding]::UTF8)
        $resolvedScope = $resolvedJson | ConvertFrom-Json
        if (-not (Test-ValidLocalScope $resolvedScope) -or
            [string]$resolvedScope.displayName -cne $displayName) {
            throw '本机只读读取器返回的会话范围未通过校验；未保存配置。'
        }

        Move-Item -LiteralPath $resolvedPath -Destination $scopePath -Force
        Save-LocalScopeApproval $resolvedScope
        return $resolvedScope
    }
    finally {
        Remove-Item -LiteralPath $requestPath,$resolvedPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-ApprovedLocalScope([string] $pythonExe) {
    $existingScope = $null
    if (Test-Path -LiteralPath $scopePath -PathType Leaf) {
        try {
            $scopeJson = [System.IO.File]::ReadAllText($scopePath, [System.Text.Encoding]::UTF8)
            $candidate = $scopeJson | ConvertFrom-Json
            if (Test-ValidLocalScope $candidate) { $existingScope = $candidate }
        }
        catch { $existingScope = $null }
    }

    if ($existingScope) {
        $expectedFingerprint = Get-ScopeFingerprint $existingScope
        $savedFingerprint = if (Test-Path -LiteralPath $approvedScopePath) { (Get-Content -Raw -LiteralPath $approvedScopePath).Trim() } else { '' }
        if ($savedFingerprint -eq $expectedFingerprint) { return $existingScope }
    }

    $wechatProcess = Get-Process -Name 'Weixin' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $wechatProcess) {
        throw '请先打开并登录微信，然后重新启动正式项目以确认所选联系人。'
    }

    if ($existingScope) {
        $displayName = Read-Host "当前配置尚未确认或已更改。请输入要使用的一对一联系人显示名（当前配置：$($existingScope.displayName)）"
    }
    else {
        $displayName = Read-Host '请输入要连接的一对一联系人显示名'
    }

    $displayName = [string]$displayName
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        throw '未选择联系人；未保存会话授权配置。'
    }

    $wechatExecutablePath = [string]$wechatProcess.Path
    $resolvedScope = Resolve-LocalContactScope $pythonExe $displayName $wechatExecutablePath
    return $resolvedScope
}

function Unprotect-LocalText([string] $value) {
    $secure = ConvertTo-SecureString -String $value
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Resolve-PostgresBin {
    $serviceFilter = 'Name=' + [char]39 + 'postgresql-x64-18' + [char]39
    $service = Get-CimInstance Win32_Service -Filter $serviceFilter -ErrorAction SilentlyContinue
    if ($service -and $service.PathName -match '(?i)([A-Z]:\\.*?\\pg_ctl\.exe)') {
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

function Test-PrivatePostgresProcessRunning {
    $normalizedDataRoot = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd('\')
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='postgres.exe'" -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $commandLine = ([string]$process.CommandLine).Replace('/', '\')
        if ($commandLine.IndexOf($normalizedDataRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
}

function Remove-StalePrivatePostgresPidFile {
    $pidPath = Join-Path $dataRoot 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { return }

    $lines = @(Get-Content -LiteralPath $pidPath -ErrorAction Stop)
    $recordedPid = 0
    $recordedPort = 0
    if (($lines.Count -lt 6) -or
        (-not [int]::TryParse([string]$lines[0], [ref]$recordedPid)) -or
        (-not [int]::TryParse([string]$lines[3], [ref]$recordedPort))) {
        throw 'The private PostgreSQL PID file is incomplete; it was left untouched.'
    }

    try { $recordedDataRoot = [System.IO.Path]::GetFullPath([string]$lines[1]).TrimEnd('\') }
    catch { throw 'The private PostgreSQL PID file has an invalid data path; it was left untouched.' }
    $normalizedDataRoot = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd('\')
    if ((-not [string]::Equals($recordedDataRoot, $normalizedDataRoot, [StringComparison]::OrdinalIgnoreCase)) -or
        ($recordedPort -ne $databasePort)) {
        throw 'The PostgreSQL PID file does not match this private database; it was left untouched.'
    }

    if (Test-PrivatePostgresProcessRunning) {
        throw 'A PostgreSQL process still references the private data directory, but pg_ctl could not verify it; no files were changed.'
    }

    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $databasePort -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        throw 'The private PostgreSQL port is occupied; the PID file was left untouched.'
    }

    Remove-Item -LiteralPath $pidPath -Force
    Write-Host '已确认私有数据库进程退出，清除了过期 PID 文件。'
}

function Ensure-LocalDatabase([string] $bin, $secrets) {
    $pgCtl = Join-Path $bin 'pg_ctl.exe'
    $initDb = Join-Path $bin 'initdb.exe'
    $psql = Join-Path $bin 'psql.exe'
    $postgres = Join-Path $bin 'postgres.exe'
    foreach ($requiredTool in @($pgCtl, $initDb, $psql, $postgres)) {
        if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
            throw 'The PostgreSQL 18 installation is incomplete; a required database executable is missing.'
        }
    }
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

    $previousErrorActionPreference = $ErrorActionPreference
    $databaseIsRunning = $false
    try {
        $ErrorActionPreference = 'Continue'
        & $pgCtl '-D' $dataRoot 'status' *> $null
        $databaseIsRunning = $LASTEXITCODE -eq 0
    }
    catch { $databaseIsRunning = $false }
    finally { $ErrorActionPreference = $previousErrorActionPreference }

    if (-not $databaseIsRunning) {
        Remove-StalePrivatePostgresPidFile
        Set-StartupStage '正在启动本机专用数据库…'
        $postgresProcess = Start-Process -FilePath $postgres -ArgumentList @('-D', $dataRoot, '-h', '127.0.0.1', '-p', "$databasePort") -WorkingDirectory $stateRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $stateRoot 'postgres.log') -RedirectStandardError (Join-Path $stateRoot 'postgres-error.log')
        $script:databaseStartedByLauncher = $true
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
            catch {
                # Windows PowerShell 5.1 may promote transient native stderr to a terminating error.
                $ready = $false
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
        $portText = [string]$databasePort
        & $psql '-X' '-q' '-v' 'ON_ERROR_STOP=1' '-h' '127.0.0.1' '-p' $portText '-U' $databaseUser '-d' $databaseName '-f' $connectionTest | Out-Null
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
            $processFilter = 'ProcessId=' + [string]$candidateId
            $candidate = Get-CimInstance Win32_Process -Filter $processFilter -ErrorAction SilentlyContinue
            if ($candidate -and $candidate.CommandLine -like ('*' + (Join-Path $apiProject 'bin\Release\net10.0\WeChatAiCopilot.Api.dll') + '*')) {
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
    $apiDll = Join-Path $apiProject 'bin\Release\net10.0\WeChatAiCopilot.Api.dll'
    $requiresBuild = -not (Test-Path -LiteralPath $apiDll)
    if (-not $requiresBuild) {
        $builtAt = (Get-Item -LiteralPath $apiDll).LastWriteTimeUtc
        $changedSource = Get-ChildItem -LiteralPath $apiProject -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.json') -and $_.LastWriteTimeUtc -gt $builtAt } | Select-Object -First 1
        $changedMigration = Get-ChildItem -LiteralPath (Join-Path $productRoot 'db\migrations') -File | Where-Object { $_.LastWriteTimeUtc -gt $builtAt } | Select-Object -First 1
        $requiresBuild = $null -ne $changedSource -or $null -ne $changedMigration
    }
    if ($requiresBuild) {
        & $dotnet 'build' (Join-Path $productRoot 'WeChatAiCopilot.sln') '-c' 'Release' '--nologo' '-v:q' *> $buildLog
        if ($LASTEXITCODE -ne 0) { throw 'The formal app could not be built. See the local build log under the application data folder.' }
    }
    if (-not (Test-Path -LiteralPath $apiDll)) { throw 'The formal app build did not produce the expected API file.' }

    $env:ASPNETCORE_URLS = $baseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:ConnectionStrings__FormalDatabase = "Host=127.0.0.1;Port=$databasePort;Database=$databaseName;Username=$databaseUser;Password=$($secrets.DatabasePassword);Timeout=15;Command Timeout=60"
    $env:Authentication__WebBearerToken = $secrets.WebToken
    $env:Authentication__ReaderBearerToken = $secrets.ReaderToken
    $env:Web__BootstrapGrantPath = Join-Path $stateRoot 'web-bootstrap.json'
    $env:Operations__StateRoot = $stateRoot

    # Optional local network egress for the AI CLI. Without network.json the app starts exactly as before.
    $networkPath = Join-Path $stateRoot 'network.json'
    if (Test-Path -LiteralPath $networkPath -PathType Leaf) {
        $network = $null
        try { $network = Get-Content -Raw -LiteralPath $networkPath | ConvertFrom-Json } catch { $network = $null }
        $proxyUrl = if ($network) { [string]$network.proxyUrl } else { '' }
        if ($proxyUrl -match '^https?://[A-Za-z0-9.\-]+(:[0-9]{1,5})?$') {
            $env:HTTP_PROXY = $proxyUrl
            $env:HTTPS_PROXY = $proxyUrl
            $env:ALL_PROXY = $proxyUrl
            $noProxy = if ($network -and -not [string]::IsNullOrWhiteSpace([string]$network.noProxy)) { [string]$network.noProxy } else { '127.0.0.1,localhost' }
            $env:NO_PROXY = $noProxy
            Write-Host "AI 连接将使用本机 network.json 指定的代理出口。"
        }
        elseif (-not [string]::IsNullOrWhiteSpace($proxyUrl)) {
            Write-Host '本机 network.json 中的代理地址格式不可用，已按直连启动。'
        }
    }

    $httpsConfigPath = Join-Path $stateRoot 'https.json'
    if (Test-Path -LiteralPath $httpsConfigPath) {
        $httpsConfig = [System.IO.File]::ReadAllText($httpsConfigPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($httpsConfig.enabled -eq $true -and $httpsConfig.origin -match '^https://[A-Za-z0-9.\-]+:5443$' -and (Test-Path -LiteralPath $httpsConfig.certificatePath)) {
            $certificatePassword = New-Object System.Net.NetworkCredential('', (ConvertTo-SecureString $httpsConfig.protectedPassword))
            $env:Kestrel__Endpoints__Local__Url = $baseUrl
            $env:Kestrel__Endpoints__Mobile__Url = 'https://0.0.0.0:5443'
            $env:Kestrel__Endpoints__Mobile__Certificate__Path = [string]$httpsConfig.certificatePath
            $env:Kestrel__Endpoints__Mobile__Certificate__Password = $certificatePassword.Password
            $env:Web__TrustedHttpsEnabled = 'true'
            $env:Web__MobileOrigin = [string]$httpsConfig.origin
            $env:Web__PublicCertificatePath = [string]$httpsConfig.publicCertificatePath
        }
    }
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
        $script:apiStartedByLauncher = $true
        Set-Content -LiteralPath $apiPidPath -Value $process.Id -Encoding ascii
    }
    finally {
        @('ASPNETCORE_URLS','ASPNETCORE_ENVIRONMENT','ConnectionStrings__FormalDatabase','Authentication__WebBearerToken','Authentication__ReaderBearerToken','Web__BootstrapGrantPath','Operations__StateRoot','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','Web__TrustedHttpsEnabled','Web__MobileOrigin','Web__PublicCertificatePath','Kestrel__Endpoints__Local__Url','Kestrel__Endpoints__Mobile__Url','Kestrel__Endpoints__Mobile__Certificate__Path','Kestrel__Endpoints__Mobile__Certificate__Password','Web__AllowedAccountExternalId','Web__AllowedConversationExternalId','Reader__AllowedAccountExternalId','Reader__AllowedConversationExternalId','Agent__Runtime','Agent__SkillVersion','Agent__RecentMessageLimit','Agent__IncludeMemory','Agent__Budget__MaxDurationSeconds','Agent__Budget__MaxContextCharacters','Agent__Budget__MaxToolCalls','Agent__AllowedPersonaPresetIds__0','Agent__AllowedPersonaPresetIds__1','Agent__AllowedPersonaPresetIds__2','Agent__AllowedPersonaPresetIds__3','Agent__AllowedReadCapabilities__0','Agent__AllowedReadCapabilities__1','Agent__AllowedReadCapabilities__2','Agent__AllowedReadCapabilities__3') | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
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

function Test-CompletedHistoryImportReceipt([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $content = [System.IO.File]::ReadAllText($path)
    $requiredFields = @(
        '"contract"\s*:\s*"HistoryBatchV1"',
        '"status"\s*:\s*"Completed"',
        '"httpStatus"\s*:\s*200',
        '"isLastBatch"\s*:\s*true',
        '"failed"\s*:\s*0'
    )
    foreach ($pattern in $requiredFields) {
        if ($content -notmatch $pattern) { return $false }
    }
    return $true
}

function Get-HistoryImportFailureCode([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $content = [System.IO.File]::ReadAllText($path)
    $match = [regex]::Match($content, '"error"\s*:\s*\{[^}]*"code"\s*:\s*"([A-Za-z0-9_-]{1,64})"')
    if ($match.Success) { return $match.Groups[1].Value.ToUpperInvariant().Replace('-', '_') }
    return $null
}

function Import-InitialHistory($scope, $secrets, [string] $pythonExe) {
    if (Test-Path -LiteralPath $historyMarker) { return }
    if (-not (Test-Path -LiteralPath $readerScript)) { throw 'The formal Reader source is missing.' }

    $readerSettings = Join-Path $readerSettingsRoot 'settings.json'
    Write-ReaderSettings $scope $secrets
    $historyLog = Join-Path $stateRoot 'reader-history.log'
    $jobId = 'local-first-' + [Guid]::NewGuid().ToString('N')
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 promotes native stderr to an error record even when
        # Reader uses stderr for its documented sanitized success receipt.
        $ErrorActionPreference = 'Continue'
        & $pythonExe $readerScript 'history' '--job-id' $jobId '--max-rows' '5000' '--batch-size' '100' *> $historyLog
        $readerExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
    if ($readerExitCode -ne 0 -and -not (Test-CompletedHistoryImportReceipt $historyLog)) {
        $failureCode = Get-HistoryImportFailureCode $historyLog
        if ($failureCode) {
            throw "The real WeChat history import failed ($failureCode); no synthetic messages were used. See the sanitized Reader status log."
        }
        throw "The real WeChat history import failed (Reader exit code $readerExitCode); no synthetic messages were used. See the sanitized Reader status log."
    }

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
    if ($script:readerStartedByLauncher -and (Test-Path -LiteralPath $readerPidPath)) {
        $readerToStop = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $readerPidPath), [ref]$readerToStop)) {
            $readerCandidate = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $readerToStop) -ErrorAction SilentlyContinue
            if ($readerCandidate -and $readerCandidate.CommandLine -like ('*' + $readerScript + '*')) { Stop-Process -Id $readerToStop -Force -ErrorAction SilentlyContinue }
        }
        Remove-Item -LiteralPath $readerPidPath -Force -ErrorAction SilentlyContinue
    }
    if ($script:apiStartedByLauncher -and (Test-Path -LiteralPath $apiPidPath)) {
        $candidateId = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $apiPidPath), [ref]$candidateId)) {
            $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$candidateId" -ErrorAction SilentlyContinue
            if ($candidate -and $candidate.CommandLine -like ('*' + (Join-Path $apiProject 'bin\Release\net10.0\WeChatAiCopilot.Api.dll') + '*')) {
                Stop-Process -Id $candidateId -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item -LiteralPath $apiPidPath -Force -ErrorAction SilentlyContinue
    }
    if (-not $bin -or -not $script:databaseStartedByLauncher) { return }
    $pidPath = Join-Path $dataRoot 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { return }

    $pgCtl = Join-Path $bin 'pg_ctl.exe'
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $pgCtl '-D' $dataRoot 'status' *> $null
        if ($LASTEXITCODE -ne 0) { return }
        & $pgCtl '-D' $dataRoot '-m' 'fast' '-w' 'stop' *> $null
        if ($LASTEXITCODE -ne 0) { Write-Host '本机数据库清理未完成；已保留数据库文件。' }
    }
    catch { Write-Host '本机数据库清理未完成；已保留数据库文件。' }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
}

$bin = $null
$startupMutex = [Threading.Mutex]::new($false, ('Local\WeChatAiCopilot.FormalStartup.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
$ownsStartupMutex = $false
try {
    try { $ownsStartupMutex = $startupMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsStartupMutex = $true }
    if (-not $ownsStartupMutex) { Write-Host '正式项目正在启动，请稍候再打开工作区。'; return }
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    Set-StartupStage '正在检查本机运行目录权限…'
    Set-PrivateDirectoryAcl $stateRoot
    Set-StartupStage '正在读取已授权会话配置…'
    Set-StartupStage '正在检查 Reader 运行环境…'
    $pythonExe = Resolve-ReaderPython311
    $readerRuntime = Join-Path $readerRoot '.runtime\package\wechatauto\db.py'
    if (-not (Test-Path -LiteralPath $readerRuntime)) {
        throw 'The verified Reader runtime has not been prepared. Contact support before continuing.'
    }

    Set-StartupStage '正在确认本机授权会话…'
    $scope = Get-ApprovedLocalScope $pythonExe
    if (-not (Test-ValidLocalScope $scope)) {
        throw 'The selected WeChat conversation configuration is incomplete.'
    }
    $scopeHistoryFingerprint = Get-ScopeFingerprint $scope
    $historyMarker = Join-Path $stateRoot ("initial-history-imported-$scopeHistoryFingerprint.json")

    Set-PrivateDirectoryAcl $readerSettingsRoot
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

    Start-ManagedReader $pythonExe
    Open-AuthenticatedWorkspace
    $stopOnExit = $false
    Write-Host ''
    Write-Host '正式工作区已启动，浏览器已自动建立本机安全会话，无需复制令牌。'
    Write-Host '只读采集正在后台运行；本窗口可以关闭。结束使用时双击“关闭正式项目.cmd”。'
    Write-Host 'Reader 日志只含状态回执，不输出微信消息正文。'
    Write-Host ''

}
catch {
    Write-Host ''
    Write-Host ('启动未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($stopOnExit -and $bin) { Stop-FormalServices $bin }
    if ($ownsStartupMutex) { $startupMutex.ReleaseMutex() }
    $startupMutex.Dispose()
}
