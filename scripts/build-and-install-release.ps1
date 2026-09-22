#Requires -Version 5.1
<#
.SYNOPSIS
  Builds a signed Release MSIX and installs it like a normal Windows app (Start Menu entry).

.DESCRIPTION
  Loads .env, ensures the package signing certificate under WINSTREAM_SECRETS_DIR/windows,
  aligns Package.appxmanifest Publisher with the cert subject, produces a self-contained
  sideload MSIX, trusts the public CER in CurrentUser\TrustedPeople, then Add-AppxPackage.

.PARAMETER SkipInstall
  Build and sign only; do not install.

.PARAMETER SkipCertTrust
  Do not import the .cer into TrustedPeople (use when already trusted).
#>
[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$SkipCertTrust,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DotEnv.ps1"

function Show-Help {
    @"
WinStream Release MSIX - build, sign, install

Usage:
  powershell -NoProfile -File scripts\build-and-install-release.ps1
  powershell -NoProfile -File scripts\build-and-install-release.ps1 -SkipInstall

Requires:
  - .env (from .env.example) with WINSTREAM_SECRETS_DIR
  - .NET 8 SDK
  - Windows 10/11 with sideloading or Developer Mode (for unsigned trust path;
    with TrustedPeople trust, Developer Mode is usually not required)

"@
}

if ($Help) {
    Show-Help
    exit 0
}

$config = Get-WinStreamPackagingConfig

Write-Host '============================================================'
Write-Host ' WinStream Release MSIX'
Write-Host '============================================================'
Write-Host " Secrets: $($config.SecretsDirectory)"
Write-Host " Cert:    $($config.CertificatePath)"
Write-Host " Config:  $($config.Configuration) | $($config.Platform)"
Write-Host ''

& "$PSScriptRoot\ensure-package-certificate.ps1"
$config = Get-WinStreamPackagingConfig

if ([string]::IsNullOrWhiteSpace($config.CertificatePassword)) {
    throw 'WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD is empty after ensure-package-certificate.'
}
if (-not (Test-Path -LiteralPath $config.CertificatePath)) {
    throw "Missing certificate: $($config.CertificatePath)"
}

# Align manifest publisher with the signing certificate subject.
$manifest = Get-Content -LiteralPath $config.ManifestPath -Raw
$manifest = [regex]::Replace(
    $manifest,
    'Publisher="[^"]*"',
    "Publisher=`"$($config.Publisher)`"")
$manifest = [regex]::Replace(
    $manifest,
    '<PublisherDisplayName>[^<]*</PublisherDisplayName>',
    "<PublisherDisplayName>$($config.PublisherDisplayName)</PublisherDisplayName>")
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($config.ManifestPath, $manifest, $utf8NoBom)
Write-Host "Manifest publisher set to: $($config.Publisher)"

New-Item -ItemType Directory -Force -Path $config.PackageOutputDirectory | Out-Null

# MSBuild often fails to open password-protected PFXs via -p:PackageCertificatePassword.
# Import into CurrentUser\My and sign by thumbprint (local sideload flow).
$secure = ConvertTo-SecureString -String $config.CertificatePassword -Force -AsPlainText
$imported = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $config.Publisher -and $_.HasPrivateKey
} | Select-Object -First 1

if (-not $imported) {
    Write-Host 'Importing package certificate into CurrentUser\My...'
    $imported = Import-PfxCertificate `
        -FilePath $config.CertificatePath `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -Password $secure `
        -Exportable
}

$thumbprint = $imported.Thumbprint
Write-Host "Signing with thumbprint: $thumbprint"

$msbuildProps = @(
    "-p:Configuration=$($config.Configuration)"
    "-p:Platform=$($config.Platform)"
    '-p:GenerateAppxPackageOnBuild=true'
    '-p:AppxPackageSigningEnabled=true'
    "-p:PackageCertificateThumbprint=$thumbprint"
    '-p:AppxBundle=Never'
    '-p:UapAppxPackageBuildMode=SideloadOnly'
    '-p:WindowsAppSDKSelfContained=true'
    "-p:AppxPackageDir=$($config.PackageOutputDirectory)\"
)

Write-Host ''
Write-Host 'Building signed MSIX (this can take a few minutes)...'
& dotnet build $config.ProjectPath @msbuildProps --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$msix = Get-ChildItem -Path $config.PackageOutputDirectory -Filter '*.msix' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msix) {
    # Some toolchains emit .msixbundle or place under AppPackages
    $msix = Get-ChildItem -Path $config.PackageOutputDirectory -Include '*.msix', '*.msixbundle' -Recurse |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $msix) {
    $fallback = Join-Path $config.RepoRoot 'WinStream\AppPackages'
    $msix = Get-ChildItem -Path $fallback -Include '*.msix', '*.msixbundle' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $msix) {
    throw "No .msix produced under $($config.PackageOutputDirectory) or WinStream\AppPackages"
}


Write-Host "Package: $($msix.FullName)"

function Install-WinStreamPackageTrust {
    param([Parameter(Mandatory = $true)][string]$CerPath)

    Write-Host 'Trusting package certificate...'

    & certutil.exe -user -addstore TrustedPeople $CerPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  Warning: could not add CER to CurrentUser\TrustedPeople'
    } else {
        Write-Host '  CurrentUser\TrustedPeople: OK'
    }

    $inMachineRoot = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $config.Publisher } |
        Select-Object -First 1

    if ($inMachineRoot) {
        Write-Host "  LocalMachine\Root: already present ($($inMachineRoot.Thumbprint))"
        return
    }

    Write-Host '  LocalMachine\Root: elevating (UAC) to trust for AppX install...'
    $proc = Start-Process -FilePath 'certutil.exe' `
        -ArgumentList @('-addstore', 'Root', $CerPath) `
        -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        throw "Failed to trust CER in LocalMachine\Root (certutil exit $($proc.ExitCode)). Install the .cer manually into Trusted Root Certification Authorities, then re-run."
    }

    $inMachineRoot = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $config.Publisher } |
        Select-Object -First 1
    if (-not $inMachineRoot) {
        throw 'CER not found in LocalMachine\Root after elevated certutil.'
    }
    Write-Host "  LocalMachine\Root: OK ($($inMachineRoot.Thumbprint))"
}

if (-not $SkipCertTrust) {
    Install-WinStreamPackageTrust -CerPath $config.CertificateCerPath
}

if ($SkipInstall) {
    Write-Host ''
    Write-Host 'SkipInstall set - package ready:'
    Write-Host "  $($msix.FullName)"
    Write-Host "  Trust CER: $($config.CertificateCerPath)"
    exit 0
}

function Install-WinStreamMsix {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-AppxPackage -Path $Path -ForceApplicationShutdown -ErrorAction Stop
}

Write-Host 'Installing (Add-AppxPackage)...'
try {
    Install-WinStreamMsix -Path $msix.FullName
} catch {
    $msg = $_.Exception.Message
    if ($msg -match '0x800B0109|root certificate') {
        Write-Host 'Root trust missing - retrying elevated trust then install...'
        Install-WinStreamPackageTrust -CerPath $config.CertificateCerPath
        Install-WinStreamMsix -Path $msix.FullName
    } elseif ($msg -match '0x80073CF3|0x80073D06|already installed|deployment') {
        Write-Host 'Existing install conflicts - removing prior WinStream packages then retrying...'
        Get-AppxPackage | Where-Object {
            $_.Name -match 'WinStream|ff3e60d4-5cf4-4f54-9fce-7c650da09ac3' -or
            $_.Publisher -eq $config.Publisher
        } | ForEach-Object {
            Write-Host "  Removing $($_.PackageFullName)"
            Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue
        }
        Install-WinStreamMsix -Path $msix.FullName
    } else {
        throw
    }
}

$installed = Get-AppxPackage | Where-Object {
    $_.Name -match 'WinStream|ff3e60d4-5cf4-4f54-9fce-7c650da09ac3'
} | Select-Object -First 1

Write-Host ''
Write-Host 'Installed successfully.'
if ($installed) {
    Write-Host "  Name:    $($installed.Name)"
    Write-Host "  Version: $($installed.Version)"
    Write-Host "  Install: $($installed.InstallLocation)"
}

Write-Host 'Launching WinStream...'
$startApp = Get-StartApps | Where-Object { $_.Name -eq 'WinStream' } | Select-Object -First 1
if ($startApp) {
    Start-Process -FilePath ("shell:AppsFolder\" + $startApp.AppID)
} else {
    Write-Host '  Could not auto-launch - open WinStream from the Start Menu.'
}

Write-Host 'Done.'
