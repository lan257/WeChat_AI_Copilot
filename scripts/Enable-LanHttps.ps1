param([Parameter(Mandatory=$true)][string] $IpAddress)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ip = $null
if (-not [Net.IPAddress]::TryParse($IpAddress, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip)) { throw '请输入本机实际局域网 IPv4 地址。' }
$localAddresses = @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
if ($IpAddress -notin $localAddresses) { throw '该 IPv4 地址不是本机网卡地址，未修改 HTTPS 设置。' }
$stateRoot = Join-Path $env:LOCALAPPDATA 'WeChatAiCopilot\Formal'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$subjectAltName = '2.5.29.17={text}IPAddress=' + $IpAddress
$certificate = New-SelfSignedCertificate -Subject 'CN=WeChat AI Copilot Local' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays(90) -TextExtension @($subjectAltName, '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
$passwordBytes = [byte[]]::new(32)
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($passwordBytes) } finally { $rng.Dispose() }
$password = ([BitConverter]::ToString($passwordBytes)).Replace('-','')
$securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
$pfxPath = Join-Path $stateRoot 'mobile-https.pfx'
$cerPath = Join-Path $stateRoot 'mobile-https.cer'
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
& $icacls $stateRoot '/inheritance:r' '/grant:r' ('*' + $sid + ':(OI)(CI)F') *> $null
if ($LASTEXITCODE -ne 0) { throw '无法保护本机证书目录，未启用 HTTPS。' }
foreach ($certificateFile in @($pfxPath, $cerPath)) {
    & $icacls $certificateFile '/inheritance:r' '/grant:r' ('*' + $sid + ':F') *> $null
    if ($LASTEXITCODE -ne 0) { throw '无法保护本机证书文件，未启用 HTTPS。' }
}
$settings = [ordered]@{ enabled = $true; origin = 'https://' + $IpAddress + ':5443'; certificatePath = $pfxPath; publicCertificatePath = $cerPath; protectedPassword = ConvertFrom-SecureString -SecureString $securePassword; expiresAtUtc = $certificate.NotAfter.ToUniversalTime().ToString('o') }
[IO.File]::WriteAllText((Join-Path $stateRoot 'https.json'), ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host 'HTTPS 配置已保存。关闭再启动正式项目后生效；未自动开放 Windows 防火墙。'
Write-Host ('公钥证书：' + $cerPath)
Write-Host '手机须先导入并信任该公钥证书，再在应用连接中心创建限时配对链接。不要分享 PFX 私钥文件。'
