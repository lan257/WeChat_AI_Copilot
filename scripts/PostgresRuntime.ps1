# Discover supported PostgreSQL installations and preserve major-version data-directory safety.

function Get-PostgresCandidates([int] $MinimumMajor = 13) {
    $binPaths = New-Object 'System.Collections.Generic.List[string]'
    $seen = @{}
    $addBinPath = {
        param([string] $path)
        if ([string]::IsNullOrWhiteSpace($path)) { return }
        $cleanPath = [System.IO.Path]::GetFullPath($path.Trim().Trim('"'))
        if (-not (Test-Path -LiteralPath (Join-Path $cleanPath 'postgres.exe') -PathType Leaf)) { return }
        $key = $cleanPath.TrimEnd('\').ToLowerInvariant()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            [void]$binPaths.Add($cleanPath)
        }
    }

    try {
        foreach ($service in @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^postgresql-x64-\d+$' })) {
            if ([string]$service.PathName -match '(?i)([A-Z]:\\.*?\\pg_ctl\.exe)(?="|\s|$)') {
                & $addBinPath (Split-Path -Parent $Matches[1])
            }
        }
    }
    catch { }

    $installRoots = @('C:\PostgreSQL', 'G:\PostgreSQL')
    if (${env:ProgramFiles}) { $installRoots += (Join-Path ${env:ProgramFiles} 'PostgreSQL') }
    if (${env:ProgramFiles(x86)}) { $installRoots += (Join-Path ${env:ProgramFiles(x86)} 'PostgreSQL') }
    foreach ($root in $installRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($versionDirectory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            & $addBinPath (Join-Path $versionDirectory.FullName 'bin')
        }
    }

    foreach ($commandName in @('postgres.exe', 'postgres')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command -and $command.CommandType -eq 'Application') { & $addBinPath (Split-Path -Parent $command.Source) }
    }

    $found = @()
    foreach ($binPath in $binPaths) {
        $requiredTools = @('postgres.exe', 'pg_ctl.exe', 'initdb.exe', 'psql.exe', 'pg_dump.exe', 'pg_restore.exe')
        if (@($requiredTools | Where-Object { -not (Test-Path -LiteralPath (Join-Path $binPath $_) -PathType Leaf) }).Count -gt 0) { continue }
        try {
            $versionOutput = @(& (Join-Path $binPath 'postgres.exe') '--version' 2>$null)
            if ($LASTEXITCODE -ne 0 -or $versionOutput.Count -eq 0) { continue }
            $versionText = ($versionOutput -join ' ').Trim()
            if ($versionText -notmatch '(?i)PostgreSQL\)\s+(\d+)\.(\d+)') { continue }
            $major = [int]$Matches[1]
            if ($major -lt $MinimumMajor) { continue }
            $found += [pscustomobject]@{ Bin = $binPath; Major = $major; Version = $versionText }
        }
        catch { }
    }

    return @($found | Group-Object Bin | ForEach-Object { $_.Group[0] } | Sort-Object `
        @{ Expression = { if ($_.Major -eq 18) { 0 } else { 1 } } }, `
        @{ Expression = { $_.Major }; Descending = $true })
}

function Get-PostgresClusterMajor([string] $dataRoot) {
    if ([string]::IsNullOrWhiteSpace($dataRoot)) { return $null }
    $versionPath = Join-Path $dataRoot 'PG_VERSION'
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { return $null }
    $versionText = [System.IO.File]::ReadAllText($versionPath).Trim()
    if ($versionText -notmatch '^\d+$') { throw 'The private PostgreSQL PG_VERSION file is invalid; its data was left untouched.' }
    return [int]$versionText
}

function Resolve-PostgresBinForDataRoot([string] $dataRoot) {
    $clusterMajor = Get-PostgresClusterMajor $dataRoot
    $candidates = @(Get-PostgresCandidates)
    if ($null -ne $clusterMajor) {
        if ($clusterMajor -lt 13) {
            throw ('The private PostgreSQL data directory uses unsupported major version ' + $clusterMajor + '. It was left untouched; migrate or restore it with PostgreSQL 13 or later.')
        }
        $matching = $candidates | Where-Object { $_.Major -eq $clusterMajor } | Select-Object -First 1
        if (-not $matching) {
            throw ('The private database uses PostgreSQL ' + $clusterMajor + ', but no matching PostgreSQL ' + $clusterMajor + ' tools are installed. Install that exact major or migrate the data directory; it was not opened with another major version.')
        }
        return $matching.Bin
    }
    if ($candidates.Count -eq 0) {
        throw 'No supported PostgreSQL 13+ installation was found. Run Setup to install the recommended PostgreSQL 18 or install a compatible major version.'
    }
    return $candidates[0].Bin
}
