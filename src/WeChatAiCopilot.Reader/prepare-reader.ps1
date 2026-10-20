param(
    [string] $PythonPath
)

$ErrorActionPreference = 'Stop'

$readerRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
. (Join-Path $readerRoot 'ReaderRuntime.ps1')
$runtimeRoot = Join-Path $readerRoot '.runtime'
$downloadDir = Join-Path $runtimeRoot 'download'
$packageDir = Join-Path $runtimeRoot 'package'
$depsDir = Join-Path $runtimeRoot 'deps'
$stagingDeps = Join-Path $runtimeRoot ('deps.preparing-' + [guid]::NewGuid().ToString('N'))
$previousDeps = Join-Path $runtimeRoot ('deps.previous-' + [guid]::NewGuid().ToString('N'))

foreach ($target in @($runtimeRoot, $downloadDir, $packageDir, $depsDir, $stagingDeps, $previousDeps)) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($readerRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reader runtime path escaped the Reader module directory.'
    }
}

New-Item -ItemType Directory -Force -Path $downloadDir, $packageDir | Out-Null
$pythonCandidates = @(Get-ReaderPythonCandidates)
$pythonCandidate = $null
if (-not [string]::IsNullOrWhiteSpace($PythonPath)) {
    $resolvedPythonPath = [System.IO.Path]::GetFullPath($PythonPath)
    $pythonCandidate = $pythonCandidates | Where-Object { $_.Path -eq $resolvedPythonPath } | Select-Object -First 1
    if (-not $pythonCandidate) { throw 'The requested Python must be an installed 64-bit Python 3.10 or later.' }
}
elseif ($pythonCandidates.Count -gt 0) {
    $pythonCandidate = $pythonCandidates[0]
}
if (-not $pythonCandidate) { throw 'Install a 64-bit Python 3.10 or later (Python 3.11 is recommended) before preparing the Reader runtime.' }
$pythonExe = $pythonCandidate.Path

& $pythonExe -m pip download --disable-pip-version-check --no-deps --only-binary=:all: --dest $downloadDir 'wechatauto-replica==1.2.7.0'
if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned read-only database module.' }

New-Item -ItemType Directory -Force -Path $stagingDeps | Out-Null
& $pythonExe -m pip install --disable-pip-version-check --upgrade --target $stagingDeps 'cryptography>=41.0.0,<49.0.0' 'zstandard>=0.19.0'
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $stagingDeps -Recurse -Force -ErrorAction SilentlyContinue
    throw 'Could not install the read-only database dependencies.'
}

& $pythonExe (Join-Path $PSScriptRoot 'extract_readonly_module.py') $downloadDir $packageDir
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $stagingDeps -Recurse -Force -ErrorAction SilentlyContinue
    throw 'The pinned database module did not pass the read-only integrity checks.'
}
if (-not (Test-ReaderRuntimeCompatibility $pythonExe $readerRoot $stagingDeps)) {
    Remove-Item -LiteralPath $stagingDeps -Recurse -Force -ErrorAction SilentlyContinue
    throw ('Reader dependencies did not pass the import/ABI check under Python ' + $pythonCandidate.Version + '.')
}

$hasPreviousDependencies = Test-Path -LiteralPath $depsDir -PathType Container
try {
    if ($hasPreviousDependencies) { Move-Item -LiteralPath $depsDir -Destination $previousDeps }
    Move-Item -LiteralPath $stagingDeps -Destination $depsDir
    if (-not (Test-ReaderRuntimeCompatibility $pythonExe $readerRoot)) {
        throw ('Reader dependencies failed the post-install import/ABI check under Python ' + $pythonCandidate.Version + '.')
    }
    if ($hasPreviousDependencies) { Remove-Item -LiteralPath $previousDeps -Recurse -Force }
}
catch {
    if (Test-Path -LiteralPath $depsDir -PathType Container) { Remove-Item -LiteralPath $depsDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($hasPreviousDependencies -and (Test-Path -LiteralPath $previousDeps -PathType Container)) {
        Move-Item -LiteralPath $previousDeps -Destination $depsDir
    }
    if (Test-Path -LiteralPath $stagingDeps -PathType Container) { Remove-Item -LiteralPath $stagingDeps -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}

Write-Host ('Reader runtime prepared and import-checked with 64-bit Python ' + $pythonCandidate.Version + '. Only the upstream database read module was extracted.')
