<#
.SYNOPSIS
    Builds the WinStream virtual audio driver package (.sys/.inf/.cat).

.DESCRIPTION
    Restores the WDK NuGet packages, builds the driver with the 64-bit MSBuild,
    and writes a build manifest describing exactly what was produced.

    The 64-bit MSBuild is required, not a preference. A 32-bit MSBuild makes the
    driver targets look for x86 copies of InfVerif and ApiValidator, which the
    Microsoft.Windows.WDK.x64 package does not ship, so the build fails after
    producing the .sys but before generating the catalog.

    This script does not install anything. See install-winstream-vad.ps1.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER TestSign
    Also sign the package with a local self-signed test certificate so it can be
    installed on a TESTSIGNING machine. Development only — never a shipping path.

.PARAMETER Clean
    Rebuild from scratch instead of building incrementally.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-winstream-vad.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-winstream-vad.ps1 -TestSign
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$TestSign,

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$driverRoot = Join-Path $repoRoot 'drivers\winstream-vad'
$solution = Join-Path $driverRoot 'WinStreamVad.sln'
$packageDir = Join-Path $driverRoot "x64\$Configuration\package"
$artifactDir = Join-Path $repoRoot 'artifacts\driver'

if (-not (Test-Path $solution)) {
    throw "Driver solution not found at $solution"
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'vswhere.exe not found. Install Visual Studio 2022 or later with "Desktop development with C++".'
    }

    $installPath = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath

    if ([string]::IsNullOrWhiteSpace($installPath)) {
        throw 'No Visual Studio install with the C++ toolset was found. Add "Desktop development with C++".'
    }

    # amd64 first: the 32-bit MSBuild resolves x86 WDK verification tools that the
    # x64-only NuGet package does not contain.
    $candidates = @(
        (Join-Path $installPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'),
        (Join-Path $installPath 'MSBuild\Current\Bin\arm64\MSBuild.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Found Visual Studio at $installPath but no 64-bit MSBuild.exe under MSBuild\Current\Bin."
}

function Restore-WdkPackages {
    param([string]$DriverRoot)

    $config = Join-Path $DriverRoot 'packages.config'
    $packages = Join-Path $DriverRoot 'packages'

    $nuget = Get-Command nuget -ErrorAction SilentlyContinue
    if ($null -eq $nuget) {
        throw @"
nuget.exe was not found on PATH and the WDK packages use packages.config, which
dotnet restore cannot handle. Install it with one of:

    winget install Microsoft.NuGet
    choco install nuget.commandline

then re-run this script.
"@
    }

    Write-Host 'Restoring WDK NuGet packages...' -ForegroundColor Cyan
    & $nuget.Source restore $config -PackagesDirectory $packages
    if ($LASTEXITCODE -ne 0) {
        throw "nuget restore failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DriverBuild {
    param(
        [string]$MSBuild,
        [string]$Solution,
        [string]$Configuration,
        [bool]$Rebuild
    )

    $target = if ($Rebuild) { 'Rebuild' } else { 'Build' }

    Write-Host "Building driver ($Configuration, target $target)..." -ForegroundColor Cyan
    & $MSBuild $Solution `
        /t:$target `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:SignMode=Off `
        /v:m `
        /nologo

    if ($LASTEXITCODE -ne 0) {
        throw "Driver build failed with exit code $LASTEXITCODE."
    }
}

function New-TestSignature {
    param([string]$PackageDir)

    $subject = 'CN=WinStream VAD Test Signing'
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $cert) {
        Write-Host 'Creating a self-signed test certificate...' -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate `
            -Subject $subject `
            -Type CodeSigningCert `
            -CertStoreLocation Cert:\CurrentUser\My `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable `
            -NotAfter (Get-Date).AddYears(2)
    }

    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $signtool) {
        throw 'signtool.exe not found under the Windows Kits bin directory.'
    }

    foreach ($name in @('WinStreamVad.sys', 'winstreamvad.cat')) {
        $file = Join-Path $PackageDir $name
        if (-not (Test-Path $file)) {
            continue
        }

        & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /t http://timestamp.digicert.com $file
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed on $name with exit code $LASTEXITCODE."
        }
    }

    Write-Host "Test-signed with thumbprint $($cert.Thumbprint)." -ForegroundColor Yellow
    Write-Host 'This is valid only on a machine with TESTSIGNING enabled.' -ForegroundColor Yellow

    return $cert.Thumbprint
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild" -ForegroundColor DarkGray

Restore-WdkPackages -DriverRoot $driverRoot
Invoke-DriverBuild -MSBuild $msbuild -Solution $solution -Configuration $Configuration -Rebuild:$Clean.IsPresent

$expected = @('WinStreamVad.sys', 'WinStreamVad.inf', 'winstreamvad.cat')
$missing = @($expected | Where-Object { -not (Test-Path (Join-Path $packageDir $_)) })
if ($missing.Count -gt 0) {
    throw "Build reported success but these package files are missing: $($missing -join ', ')"
}

$thumbprint = $null
if ($TestSign) {
    $thumbprint = New-TestSignature -PackageDir $packageDir
}

$inf = Get-Content (Join-Path $packageDir 'WinStreamVad.inf')
$driverVer = ($inf | Select-String -Pattern '^\s*DriverVer\s*=' | Select-Object -First 1).Line.Split('=', 2)[1].Trim()

$files = foreach ($name in $expected) {
    $path = Join-Path $packageDir $name
    [pscustomobject]@{
        name   = $name
        bytes  = (Get-Item $path).Length
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash
    }
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$manifestPath = Join-Path $artifactDir 'build-manifest.json'

[pscustomobject]@{
    builtUtc        = (Get-Date).ToUniversalTime().ToString('o')
    configuration   = $Configuration
    platform        = 'x64'
    driverVer       = $driverVer
    rootHardwareId  = 'ROOT\WINSTREAMVAD'
    packageDir      = $packageDir
    msbuild         = $msbuild
    testSigned      = [bool]$TestSign
    testCertificate = $thumbprint
    files           = $files
} | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host ''
Write-Host 'Driver package built.' -ForegroundColor Green
Write-Host "  Package:  $packageDir"
Write-Host "  DriverVer: $driverVer"
Write-Host "  Manifest: $manifestPath"
Write-Host ''
Write-Host 'Building the package does not prove the 3 ms capture contract.' -ForegroundColor Yellow
Write-Host 'Install it on a disposable TESTSIGNING VM and run tools/VadProbe to measure.' -ForegroundColor Yellow
