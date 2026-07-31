# Shared dotenv loader for WinStream packaging scripts.
# Dot-source: . "$PSScriptRoot\DotEnv.ps1"

function Import-WinStreamDotEnv {
    param(
        [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env')
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    Get-Content -LiteralPath $Path | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith('#')) {
            return
        }

        $idx = $line.IndexOf('=')
        if ($idx -lt 1) {
            return
        }

        $key = $line.Substring(0, $idx).Trim()
        $value = $line.Substring($idx + 1).Trim()

        # Strip optional surrounding quotes
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        # Expand ${VAR} references using already-loaded process env
        $value = [regex]::Replace($value, '\$\{([A-Za-z_][A-Za-z0-9_]*)\}', {
            param($m)
            $name = $m.Groups[1].Value
            $existing = [Environment]::GetEnvironmentVariable($name)
            if ([string]::IsNullOrEmpty($existing)) { $m.Value } else { $existing }
        })

        Set-Item -Path "Env:$key" -Value $value
    }

    return $true
}

function Get-WinStreamPackagingConfig {
    param(
        [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
    )

    $null = Import-WinStreamDotEnv -Path (Join-Path $RepoRoot '.env')

    $secretsDir = $env:WINSTREAM_SECRETS_DIR
    if ([string]::IsNullOrWhiteSpace($secretsDir)) {
        $secretsDir = 'C:\path\to\your\.secrets'
    }

    $windowsDir = Join-Path $secretsDir 'windows'
    $pfx = $env:WINSTREAM_PACKAGE_CERTIFICATE_PATH
    if ([string]::IsNullOrWhiteSpace($pfx)) {
        $pfx = Join-Path $windowsDir 'winstream-package.pfx'
    }

    $cer = $env:WINSTREAM_PACKAGE_CERTIFICATE_CER_PATH
    if ([string]::IsNullOrWhiteSpace($cer)) {
        $cer = Join-Path $windowsDir 'winstream-package.cer'
    }

    $publisher = $env:WINSTREAM_PACKAGE_PUBLISHER
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        $publisher = 'CN=WinStream Dev, O=Local Development, C=US'
    }

    $publisherDisplay = $env:WINSTREAM_PACKAGE_PUBLISHER_DISPLAY_NAME
    if ([string]::IsNullOrWhiteSpace($publisherDisplay)) {
        $publisherDisplay = 'WinStream Dev'
    }

    $platform = $env:WINSTREAM_PLATFORM
    if ([string]::IsNullOrWhiteSpace($platform)) {
        $platform = 'x64'
    }

    $configuration = $env:WINSTREAM_CONFIGURATION
    if ([string]::IsNullOrWhiteSpace($configuration)) {
        $configuration = 'Release'
    }

    [pscustomobject]@{
        RepoRoot                 = $RepoRoot
        SecretsDirectory         = $secretsDir
        WindowsSecretsDirectory  = $windowsDir
        CertificatePath          = $pfx
        CertificateCerPath       = $cer
        CertificatePassword      = $env:WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD
        Publisher                = $publisher
        PublisherDisplayName     = $publisherDisplay
        Platform                 = $platform
        Configuration            = $configuration
        ProjectPath              = Join-Path $RepoRoot 'WinStream\WinStream.csproj'
        ManifestPath             = Join-Path $RepoRoot 'WinStream\Package.appxmanifest'
        PackageOutputDirectory   = Join-Path $RepoRoot 'artifacts\msix'
    }
}

function Write-WinStreamDotEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value,
        [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env')
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing $Path - copy .env.example to .env first."
    }

    $lines = Get-Content -LiteralPath $Path
    $found = $false
    $updated = foreach ($line in $lines) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=") {
            $found = $true
            "$Key=$Value"
        } else {
            $line
        }
    }

    if (-not $found) {
        $updated += "$Key=$Value"
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllLines($Path, $updated, $utf8NoBom)
    Set-Item -Path "Env:$Key" -Value $Value
}
