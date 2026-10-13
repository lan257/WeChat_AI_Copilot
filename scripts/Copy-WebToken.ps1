$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$secretsPath = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal\secrets.json'
$secure = $null
$pointer = [IntPtr]::Zero
$token = $null

try {
    if (-not (Test-Path -LiteralPath $secretsPath -PathType Leaf)) {
        throw 'The formal project has not created local credentials yet.'
    }

    $stored = Get-Content -Raw -LiteralPath $secretsPath | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$stored.webToken)) {
        throw 'The stored Web token is unavailable.'
    }

    $secure = ConvertTo-SecureString -String ([string]$stored.webToken)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'The stored Web token is unavailable.'
    }

    Set-Clipboard -Value $token
    Write-Host 'Web Bearer token copied to the clipboard. Paste it into the local Web token field without adding a prefix.'
}
catch {
    Write-Host 'Could not copy the Web token. Start the formal project once, then try again.' -ForegroundColor Red
    exit 1
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ($secure) { $secure.Dispose() }
    Remove-Variable token,stored -ErrorAction SilentlyContinue
}
