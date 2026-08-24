<#
.SYNOPSIS
Generates new local AirFerry Android and Chrome release credentials.

.DESCRIPTION
Writes private keys only under git-ignored dist/. The metadata needed to upload
GitHub Actions Secrets is protected as a binary DPAPI CurrentUser payload.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
$Dist = Join-Path $Root "dist"
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

$keystore = Join-Path $Dist "airferry-release.keystore"
$pem = Join-Path $Dist "airferry-extension.pem"
$store = Join-Path $Dist "release-secrets.dpapi"
foreach ($file in @($keystore, $pem, $store)) {
    if (Test-Path -LiteralPath $file) {
        throw "Refusing to overwrite existing release material: $file"
    }
}

$alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
$random = New-Object byte[] 40
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
$password = -join ($random | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
$keytool = (Get-Command keytool.exe -ErrorAction Stop).Source
$openssl = (Get-Command openssl.exe -ErrorAction Stop).Source

& $keytool -genkeypair -v -keystore $keystore -storetype PKCS12 `
    -storepass $password -keypass $password -alias airferry `
    -keyalg RSA -keysize 4096 -validity 9125 `
    -dname "CN=AirFerry Release, OU=AirFerry, O=AirFerry, L=Offline, S=Offline, C=US" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "keytool failed to generate the Android release keystore" }

& $openssl genrsa -out $pem 2048
if ($LASTEXITCODE -ne 0) { throw "openssl failed to generate the Chrome extension key" }

$certificate = & $keytool -list -v -keystore $keystore -storepass $password -alias airferry |
    Where-Object { $_ -match "SHA256:" } | Select-Object -First 1
$androidFingerprint = ($certificate -replace ".*SHA256:\s*", "").Replace(":", "")
if ($androidFingerprint -notmatch "^[A-F0-9]{64}$") { throw "Could not read the Android certificate fingerprint" }
$publicDer = [IO.Path]::GetTempFileName()
try {
    # OpenSSL writes a successful "writing RSA key" diagnostic to stderr, which
    # PowerShell treats as an error under ErrorActionPreference=Stop. Hash a
    # temporary DER file instead of piping OpenSSL's binary stdout.
    & cmd.exe /d /s /c "`"$openssl`" rsa -in `"$pem`" -pubout -outform DER -out `"$publicDer`" 2>nul"
    if ($LASTEXITCODE -ne 0) { throw "openssl failed to export the Chrome public key" }
    $chromePublicHash = (Get-FileHash -LiteralPath $publicDer -Algorithm SHA256).Hash.ToLowerInvariant()
} finally {
    Remove-Item -LiteralPath $publicDer -Force -ErrorAction SilentlyContinue
}
if ($chromePublicHash -notmatch "^[a-f0-9]{64}$") { throw "Could not calculate the Chrome public key fingerprint" }

$values = [ordered]@{
    androidKeystoreBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keystore))
    androidStorePassword = $password
    androidKeyAlias = "airferry"
    androidKeyPassword = $password
    chromeExtensionPemBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pem))
    androidCertificateSha256 = $androidFingerprint
    chromePublicKeySha256 = $chromePublicHash
}
$secretJson = $values | ConvertTo-Json -Compress
$secretSecure = ConvertTo-SecureString $secretJson -AsPlainText -Force
Export-Clixml -LiteralPath $store -InputObject $secretSecure -Force
cmdkey /generic:AirFerry-Release-Android /user:airferry /pass:$password | Out-Null

Write-Host "Android certificate SHA-256: $androidFingerprint"
Write-Host "Chrome public key SHA-256: $chromePublicHash"
Write-Host "Release credentials written under dist/ and protected with user DPAPI."
