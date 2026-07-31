<#
.SYNOPSIS
    Installs or removes the test-signed WinStream virtual audio driver.

.DESCRIPTION
    Wraps pnputil with the preflight checks that make a driver install survivable:
    elevation, TESTSIGNING state, Secure Boot and HVCI interaction, and a package
    that actually exists.

    This modifies the running system's driver store. Use a disposable VM with a
    snapshot. Nothing here is a shipping install path — the production installer
    uses SetupAPI and a properly signed package.

.PARAMETER Uninstall
    Remove the device and delete the driver package instead of installing.

.PARAMETER PackageDir
    Package directory. Defaults to the Release output of build-winstream-vad.ps1.

.PARAMETER Force
    Skip the interactive confirmation. Intended for automation on a throwaway VM.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-winstream-vad.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-winstream-vad.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$PackageDir,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hardwareId = 'ROOT\WINSTREAMVAD'
$infName = 'WinStreamVad.inf'

if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    $PackageDir = Join-Path $repoRoot 'drivers\winstream-vad\x64\Release\package'
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run from an elevated PowerShell session.'
    }
}

function Test-TestSigning {
    $output = & bcdedit /enum '{current}' 2>&1 | Out-String
    return $output -match '(?im)^\s*testsigning\s+Yes\s*$'
}

function Get-SecureBootState {
    try {
        return [bool](Confirm-SecureBootUEFI)
    }
    catch {
        return $false
    }
}

function Test-HvciRunning {
    try {
        $guard = Get-CimInstance -ClassName Win32_DeviceGuard `
            -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction Stop
        # 2 == hypervisor-enforced code integrity running.
        return @($guard.SecurityServicesRunning) -contains 2
    }
    catch {
        return $false
    }
}

function Write-Preflight {
    $testSigning = Test-TestSigning
    $secureBoot = Get-SecureBootState
    $hvci = Test-HvciRunning

    Write-Host 'Preflight' -ForegroundColor Cyan
    Write-Host ("  TESTSIGNING : {0}" -f $(if ($testSigning) { 'on' } else { 'OFF' })) `
        -ForegroundColor $(if ($testSigning) { 'Green' } else { 'Red' })
    Write-Host ("  Secure Boot : {0}" -f $(if ($secureBoot) { 'on' } else { 'off' })) `
        -ForegroundColor $(if ($secureBoot) { 'Yellow' } else { 'Green' })
    Write-Host ("  HVCI        : {0}" -f $(if ($hvci) { 'running' } else { 'off' })) `
        -ForegroundColor $(if ($hvci) { 'Yellow' } else { 'Green' })

    if (-not $testSigning) {
        throw @"
TESTSIGNING is off, so Windows will refuse this test-signed package.

    bcdedit /set testsigning on

then reboot and re-run. Turn it back off when you are done:

    bcdedit /set testsigning off
"@
    }

    if ($secureBoot) {
        throw @"
Secure Boot is enabled, which overrides TESTSIGNING and blocks test-signed
drivers. Disable Secure Boot in the VM firmware settings first.
"@
    }

    if ($hvci) {
        Write-Warning @"
Memory integrity (HVCI) is running and may block an unsigned or test-signed
driver from loading even though the package installs. If the device appears
with a yellow bang, turn off Core isolation > Memory integrity and reboot.
"@
    }
}

function Invoke-Uninstall {
    Write-Host "Removing devices matching $hardwareId..." -ForegroundColor Cyan
    & pnputil /remove-device /deviceid $hardwareId /subtree 2>&1 | Write-Host

    $installed = & pnputil /enum-drivers 2>&1 | Out-String
    $matches = [regex]::Matches(
        $installed,
        '(?ims)Published Name:\s*(oem\d+\.inf).*?Original Name:\s*WinStreamVad\.inf')

    if ($matches.Count -eq 0) {
        Write-Host 'No WinStreamVad package found in the driver store.' -ForegroundColor Yellow
        return
    }

    foreach ($match in $matches) {
        $published = $match.Groups[1].Value
        Write-Host "Deleting driver package $published..." -ForegroundColor Cyan
        & pnputil /delete-driver $published /uninstall /force 2>&1 | Write-Host
    }

    Write-Host 'Uninstall complete. Reboot to be certain the endpoint is gone.' -ForegroundColor Green
}

function Invoke-Install {
    $inf = Join-Path $PackageDir $infName
    if (-not (Test-Path $inf)) {
        throw "Driver package not found at $inf. Run scripts\build-winstream-vad.ps1 -TestSign first."
    }

    $cat = Join-Path $PackageDir 'winstreamvad.cat'
    $signature = Get-AuthenticodeSignature $cat
    if ($signature.Status -ne 'Valid') {
        Write-Warning "Catalog signature status is '$($signature.Status)'. Re-run the build with -TestSign."
    }

    Write-Host "Installing $inf..." -ForegroundColor Cyan
    & pnputil /add-driver $inf /install 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil /add-driver failed with exit code $LASTEXITCODE."
    }

    # A root-enumerated device is not created by /install alone on every OS build.
    $existing = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like "$hardwareId*" }

    if ($null -eq $existing) {
        Write-Host 'Creating the root-enumerated device node...' -ForegroundColor Cyan
        & pnputil /add-device /hardwareid $hardwareId 2>&1 | Write-Host
    }

    Start-Sleep -Seconds 2
    $device = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like "$hardwareId*" } |
        Select-Object -First 1

    if ($null -eq $device) {
        throw "Package installed but no $hardwareId device appeared. Check Device Manager > View > Show hidden devices."
    }

    Write-Host ''
    Write-Host "Device: $($device.InstanceId)" -ForegroundColor Green
    Write-Host "Status: $($device.Status)" -ForegroundColor $(if ($device.Status -eq 'OK') { 'Green' } else { 'Red' })

    if ($device.Status -ne 'OK') {
        Write-Warning 'Device is present but not started. HVCI or an unsigned catalog is the usual cause.'
    }

    Write-Host ''
    Write-Host 'Next: measure the real capture period, do not assume it.' -ForegroundColor Yellow
    Write-Host '  dotnet run --project tools\VadProbe\VadProbe.csproj -c Release' -ForegroundColor Yellow
}

Assert-Elevated

$action = if ($Uninstall) { 'UNINSTALL the WinStream virtual audio driver' } else { 'INSTALL a test-signed kernel driver' }
if (-not $Force) {
    Write-Host ''
    Write-Warning "About to $action on this machine."
    Write-Warning 'Only do this on a disposable VM with a snapshot you can roll back to.'
    $answer = Read-Host 'Type YES to continue'
    if ($answer -cne 'YES') {
        Write-Host 'Aborted.' -ForegroundColor Yellow
        return
    }
}

if ($Uninstall) {
    Invoke-Uninstall
}
else {
    Write-Preflight
    Invoke-Install
}
