$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$productRoot = Split-Path -Parent $PSScriptRoot
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
$readerScript = Join-Path $productRoot 'src\WeChatAiCopilot.Reader\reader.py'
$apiDll = Join-Path $productRoot 'src\WeChatAiCopilot.Api\bin\Release\net10.0\WeChatAiCopilot.Api.dll'
$apiPublishedExe = Join-Path $productRoot 'artifacts\app\win-x64\WeChatAiCopilot.Api.exe'

# The API runs either as "dotnet <dll>" or as the self-contained published executable.
function Test-OwnedProcess($candidate, $needles) {
    if (-not $candidate) { return $false }
    foreach ($needle in $needles) { if ($candidate.CommandLine -like ('*' + $needle + '*')) { return $true } }
    return $false
}

foreach ($entry in @(@{ file = 'reader.pid'; owned = @($readerScript) }, @{ file = 'api.pid'; owned = @($apiDll, $apiPublishedExe) })) {
    $pidPath = Join-Path $stateRoot $entry.file
    if (-not (Test-Path -LiteralPath $pidPath)) { continue }
    $ownedProcessId = 0
    if ([int]::TryParse((Get-Content -LiteralPath $pidPath -Raw), [ref]$ownedProcessId)) {
        $candidate = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ownedProcessId) -ErrorAction SilentlyContinue
        if (Test-OwnedProcess $candidate $entry.owned) {
            if ($entry.file -eq 'reader.pid') {
                $stopRequest = Join-Path $stateRoot ('reader-stop-' + $ownedProcessId + '.request')
                [IO.File]::WriteAllText($stopRequest, 'stop', [Text.UTF8Encoding]::new($false))
                $deadline = [DateTime]::UtcNow.AddSeconds(20)
                do {
                    Start-Sleep -Milliseconds 250
                    $candidate = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ownedProcessId) -ErrorAction SilentlyContinue
                } while ($candidate -and [DateTime]::UtcNow -lt $deadline)
                if (Test-OwnedProcess $candidate $entry.owned) { Stop-Process -Id $ownedProcessId -Force -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $stopRequest -Force -ErrorAction SilentlyContinue
            }
            else { Stop-Process -Id $ownedProcessId -Force -ErrorAction SilentlyContinue }
        }
    }
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
}
$dataRoot = Join-Path $stateRoot 'postgres-data'
$databasePidPath = Join-Path $dataRoot 'postmaster.pid'
if (Test-Path -LiteralPath $databasePidPath) {
    $pidLines = @(Get-Content -LiteralPath $databasePidPath)
    $databaseProcessId = 0
    if ($pidLines.Count -gt 1 -and [int]::TryParse($pidLines[0], [ref]$databaseProcessId)) {
        $recordedRoot = [IO.Path]::GetFullPath($pidLines[1]).TrimEnd('\','/')
        if ($recordedRoot -eq [IO.Path]::GetFullPath($dataRoot).TrimEnd('\','/')) {
            $databaseProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $databaseProcessId) -ErrorAction SilentlyContinue
            if ($databaseProcess -and $databaseProcess.Name -eq 'postgres.exe' -and $databaseProcess.CommandLine -like ('*' + $dataRoot + '*')) {
                $pgCtl = Join-Path (Split-Path -Parent $databaseProcess.ExecutablePath) 'pg_ctl.exe'
                $previousPreference = $ErrorActionPreference
                try { $ErrorActionPreference = 'Continue'; & $pgCtl '-D' $dataRoot '-m' 'fast' '-w' 'stop' *> $null }
                finally { $ErrorActionPreference = $previousPreference }
                if ($LASTEXITCODE -ne 0) { throw '本机数据库尚未完整关闭；已保留数据，请稍后重试。' }
            }
        }
    }
}
Remove-Item -LiteralPath (Join-Path $stateRoot 'web-bootstrap.json') -Force -ErrorAction SilentlyContinue
Write-Host '正式项目已关闭；本机数据已保留。'
