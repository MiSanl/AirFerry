<#
.SYNOPSIS
Uploads the locally protected AirFerry release credentials to GitHub Actions.

.DESCRIPTION
The source credentials live under dist/ (git-ignored). release-secrets.dpapi
contains their Base64 payload and is encrypted with the current Windows user's
DPAPI profile. This script never prints passwords, private keys, or Base64 data.
#>

[CmdletBinding()]
param(
    [string]$Repository = "MiSanl/AirFerry",
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
$SecretFile = Join-Path $Root "dist/release-secrets.dpapi"

if (-not (Test-Path -LiteralPath $SecretFile)) {
    throw "Release secret store is missing: $SecretFile"
}
if (-not $VerifyOnly -and -not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required. Install it, then run gh auth login."
}

if (-not $VerifyOnly) {
    gh auth status --hostname github.com
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run: gh auth login --hostname github.com --git-protocol ssh"
    }
}

# ReadAllText preserves a UTF-8 BOM as U+FEFF on Windows PowerShell 5.1.
# ConvertTo-SecureString requires the encrypted payload to begin at byte zero.
$protected = [IO.File]::ReadAllText($SecretFile, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF)
$secure = ConvertTo-SecureString $protected
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $json = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
$values = $json | ConvertFrom-Json

$secrets = [ordered]@{
    ANDROID_KEYSTORE_BASE64 = $values.androidKeystoreBase64
    ANDROID_STORE_PASSWORD  = $values.androidStorePassword
    ANDROID_KEY_ALIAS       = $values.androidKeyAlias
    ANDROID_KEY_PASSWORD    = $values.androidKeyPassword
    CHROME_EXTENSION_PEM_BASE64 = $values.chromeExtensionPemBase64
}

if ($VerifyOnly) {
    if ($secrets.Values | Where-Object { [string]::IsNullOrWhiteSpace($_) }) {
        throw "The local release secret store contains an empty required value."
    }
    Write-Host "Local DPAPI release credentials decrypted and validated."
    return
}

foreach ($entry in $secrets.GetEnumerator()) {
    gh secret set $entry.Key --repo $Repository --body $entry.Value
    if ($LASTEXITCODE -ne 0) { throw "Failed to set GitHub secret: $($entry.Key)" }
}

Write-Host "Configured $($secrets.Count) GitHub Actions release secrets for $Repository."
