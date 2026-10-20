# Shared Python discovery and native-dependency compatibility checks for the Windows Reader.

function Get-ReaderPythonCandidates {
    $paths = New-Object 'System.Collections.Generic.List[string]'
    $seen = @{}
    $addPath = {
        param([string] $path)
        if ([string]::IsNullOrWhiteSpace($path)) { return }
        $cleanPath = $path.Trim().Trim('"')
        if (-not (Test-Path -LiteralPath $cleanPath -PathType Leaf)) { return }
        $key = $cleanPath.ToLowerInvariant()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            [void]$paths.Add($cleanPath)
        }
    }

    foreach ($commandName in @('python.exe', 'python')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command -and $command.CommandType -eq 'Application') { & $addPath $command.Source }
    }

    $launcher = Get-Command py.exe, py -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($launcher) {
        try {
            foreach ($line in @(& $launcher.Source '-0p' 2>$null)) {
                if ([string]$line -match '(?i)([A-Z]:\\.*?pythonw?\.exe)\s*$') { & $addPath $Matches[1] }
            }
        }
        catch { }
    }

    $pythonRoots = @()
    if ($env:LOCALAPPDATA) { $pythonRoots += (Join-Path $env:LOCALAPPDATA 'Programs\Python') }
    if (${env:ProgramFiles}) { $pythonRoots += (Join-Path ${env:ProgramFiles} 'Python') }
    if (${env:ProgramFiles(x86)}) { $pythonRoots += (Join-Path ${env:ProgramFiles(x86)} 'Python') }
    foreach ($root in $pythonRoots) {
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            & $addPath (Join-Path $directory.FullName 'python.exe')
        }
    }
    foreach ($version in @('310', '311', '312', '313', '314')) {
        & $addPath (Join-Path $env:SystemDrive ("Python{0}\python.exe" -f $version))
    }

    $found = @()
    $probe = 'import json,struct,sys; print(json.dumps({"major":sys.version_info[0],"minor":sys.version_info[1],"bits":struct.calcsize("P")*8,"executable":sys.executable}))'
    foreach ($path in $paths) {
        try {
            $output = @(& $path '-I' '-c' $probe 2>$null)
            if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) { continue }
            $identity = ([string]$output[-1]) | ConvertFrom-Json -ErrorAction Stop
            if ([int]$identity.major -ne 3 -or [int]$identity.minor -lt 10 -or [int]$identity.bits -ne 64) { continue }
            $executable = [string]$identity.executable
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { continue }
            $found += [pscustomobject]@{
                Path = $executable
                Major = [int]$identity.major
                Minor = [int]$identity.minor
                Version = '{0}.{1}' -f $identity.major, $identity.minor
            }
        }
        catch { }
    }

    return @($found | Group-Object Path | ForEach-Object { $_.Group[0] } | Sort-Object `
        @{ Expression = { if ($_.Major -eq 3 -and $_.Minor -eq 11) { 0 } else { 1 } } }, `
        @{ Expression = { $_.Major }; Descending = $true }, `
        @{ Expression = { $_.Minor }; Descending = $true })
}

function Test-ReaderRuntimeCompatibility([string] $pythonPath, [string] $readerRoot, [string] $dependencyRoot) {
    if ([string]::IsNullOrWhiteSpace($pythonPath) -or [string]::IsNullOrWhiteSpace($readerRoot)) { return $false }
    $packageDir = Join-Path $readerRoot '.runtime\package'
    $depsDir = if ([string]::IsNullOrWhiteSpace($dependencyRoot)) { Join-Path $readerRoot '.runtime\deps' } else { $dependencyRoot }
    $modulePath = Join-Path $packageDir 'wechatauto\db.py'
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { return $false }
    try {
        $probe = 'import sys; package_dir,deps_dir=sys.argv[1:3]; sys.path[:0]=[deps_dir,package_dir]; import cryptography,zstandard; from wechatauto.db import WeChatDB; assert callable(getattr(WeChatDB,"sender_username",None))'
        & $pythonPath '-I' '-c' $probe $packageDir $depsDir *> $null
        return $LASTEXITCODE -eq 0
    }
    catch { return $false }
}
