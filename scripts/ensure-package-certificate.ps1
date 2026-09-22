#Requires -Version 5.1
<#
.SYNOPSIS
  Ensures a WinStream MSIX package-signing certificate exists under WINSTREAM_SECRETS_DIR/windows.

.DESCRIPTION
  Creates a self-signed code-signing PFX + CER when missing (local sideload trust).
  Reads/writes password via this repo's .env (WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD).
  Does not print the password.
#>
[CmdletBinding()]
param(
    [switch]$ForceRecreate
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DotEnv.ps1"

$config = Get-WinStreamPackagingConfig
$envPath = Join-Path $config.RepoRoot '.env'

if (-not (Test-Path -LiteralPath $envPath)) {
    Copy-Item -LiteralPath (Join-Path $config.RepoRoot '.env.example') -Destination $envPath
    Write-Host "Created $envPath from .env.example"
    $config = Get-WinStreamPackagingConfig
}

New-Item -ItemType Directory -Force -Path $config.WindowsSecretsDirectory | Out-Null

$password = $config.CertificatePassword
if ([string]::IsNullOrWhiteSpace($password)) {
    # 24-char URL-safe secret; stored only in .env / used for PFX export
    $bytes = New-Object byte[] 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    Write-WinStreamDotEnvValue -Key 'WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD' -Value $password -Path $envPath
    Write-Host 'Generated WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD in .env'
    $config = Get-WinStreamPackagingConfig
    $password = $config.CertificatePassword
}

$pfxExists = Test-Path -LiteralPath $config.CertificatePath
$cerExists = Test-Path -LiteralPath $config.CertificateCerPath

if ($pfxExists -and $cerExists -and -not $ForceRecreate) {
    Write-Host "Using existing package certificate:"
    Write-Host "  PFX: $($config.CertificatePath)"
    Write-Host "  CER: $($config.CertificateCerPath)"
    Write-Host "  Publisher: $($config.Publisher)"
    exit 0
}

if ($ForceRecreate) {
    Remove-Item -LiteralPath $config.CertificatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $config.CertificateCerPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'Creating self-signed WinStream package certificate...'
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $config.Publisher `
    -KeyUsage DigitalSignature `
    -FriendlyName 'WinStream Package Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
    -NotAfter (Get-Date).AddYears(5)

try {
    $secure = ConvertTo-SecureString -String $password -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $config.CertificatePath -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $config.CertificateCerPath | Out-Null
}
finally {
    Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
}

Write-Host "Created:"
Write-Host "  $($config.CertificatePath)"
Write-Host "  $($config.CertificateCerPath)"
Write-Host "  Subject: $($config.Publisher)"
Write-Host "Done."
