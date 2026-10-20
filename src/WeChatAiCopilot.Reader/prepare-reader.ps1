$ErrorActionPreference = 'Stop'

$readerRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$runtimeRoot = Join-Path $readerRoot '.runtime'
$downloadDir = Join-Path $runtimeRoot 'download'
$packageDir = Join-Path $runtimeRoot 'package'
$depsDir = Join-Path $runtimeRoot 'deps'

foreach ($target in @($runtimeRoot, $downloadDir, $packageDir, $depsDir)) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($readerRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reader runtime path escaped the Reader module directory.'
    }
}

New-Item -ItemType Directory -Force -Path $downloadDir, $packageDir, $depsDir | Out-Null
$pythonExe = (Get-Command python -ErrorAction SilentlyContinue).Source
$pythonArgs = @()
if ($pythonExe) {
    $pythonVersion = & $pythonExe --version 2>&1
    if ($pythonVersion -notmatch '^Python 3\.11\.') { $pythonExe = $null }
}
if (-not $pythonExe) {
    $launcher = (Get-Command py -ErrorAction SilentlyContinue).Source
    if ($launcher) {
        $pythonExe = $launcher
        $pythonArgs = @('-3.11')
    }
}
if (-not $pythonExe) { throw 'Python 3.11 is required to prepare the Reader runtime.' }

& $pythonExe @pythonArgs -m pip download --disable-pip-version-check --no-deps --only-binary=:all: --dest $downloadDir 'wechatauto-replica==1.2.7.0'
if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned read-only database module.' }

& $pythonExe @pythonArgs -m pip install --disable-pip-version-check --upgrade --target $depsDir 'cryptography>=41.0.0,<49.0.0' 'zstandard>=0.19.0'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the read-only database dependencies.' }

& $pythonExe @pythonArgs (Join-Path $PSScriptRoot 'extract_readonly_module.py') $downloadDir $packageDir
if ($LASTEXITCODE -ne 0) { throw 'The pinned database module did not pass the read-only integrity checks.' }

Write-Host 'Reader runtime prepared in this module. Only the upstream database read module was extracted.'
