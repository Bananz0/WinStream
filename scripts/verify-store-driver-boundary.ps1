[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string[]]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$forbiddenExtensions = @('.sys', '.inf', '.cat', '.pfx', '.cab', '.hlkx')
$violations = [System.Collections.Generic.List[string]]::new()

function Test-ForbiddenName {
    param([Parameter(Mandatory)][string]$Name)

    $normalized = $Name.Replace('\', '/')
    $extension = [System.IO.Path]::GetExtension($normalized).ToLowerInvariant()
    return $forbiddenExtensions -contains $extension -or
        [System.IO.Path]::GetFileName($normalized) -like '*DriverInstaller*.exe'
}

function Test-Archive {
    param(
        [Parameter(Mandatory)][System.IO.Stream]$Stream,
        [Parameter(Mandatory)][string]$Label,
        [int]$Depth = 0
    )

    $archive = [System.IO.Compression.ZipArchive]::new(
        $Stream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $true)
    try {
        foreach ($entry in $archive.Entries) {
            if (Test-ForbiddenName $entry.FullName) {
                $violations.Add("$Label::$($entry.FullName)")
            }

            $extension = [System.IO.Path]::GetExtension($entry.FullName).ToLowerInvariant()
            if ($Depth -lt 2 -and $extension -in @('.appx', '.msix')) {
                $nested = [System.IO.MemoryStream]::new()
                try {
                    $entryStream = $entry.Open()
                    try {
                        $entryStream.CopyTo($nested)
                    }
                    finally {
                        $entryStream.Dispose()
                    }

                    $nested.Position = 0
                    Test-Archive -Stream $nested -Label "$Label::$($entry.FullName)" -Depth ($Depth + 1)
                }
                finally {
                    $nested.Dispose()
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$repository = (Resolve-Path $RepositoryRoot).Path
$appRoot = Join-Path $repository 'WinStream'
if (-not (Test-Path $appRoot -PathType Container)) {
    throw "WinStream app directory not found under '$repository'."
}

# Check authored app inputs. Driver source is intentionally allowed under drivers/.
Get-ChildItem $appRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|AppPackages)[\\/]' -and
        (Test-ForbiddenName $_.Name)
    } |
    ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($repository, $_.FullName)
        $violations.Add("source::$relative")
    }

foreach ($projectFile in @(
    (Join-Path $appRoot 'WinStream.csproj'),
    (Join-Path $appRoot 'Package.appxmanifest')
)) {
    if (-not (Test-Path $projectFile -PathType Leaf)) {
        continue
    }

    $content = Get-Content $projectFile -Raw
    if ($content -match '(?i)(winstream\.driverinstaller|drivers[\\/]+winstream-vad|\.sys\b|\.inf\b|\.cat\b)') {
        $relative = [System.IO.Path]::GetRelativePath($repository, $projectFile)
        $violations.Add("reference::$relative")
    }
}

$packages = [System.Collections.Generic.List[string]]::new()
foreach ($candidate in @($PackagePath)) {
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        continue
    }

    $resolved = Resolve-Path $candidate
    if ($resolved) {
        $packages.Add($resolved.Path)
    }
}

if ($packages.Count -eq 0) {
    foreach ($root in @(
        (Join-Path $appRoot 'AppPackages'),
        (Join-Path $repository 'AppPackages')
    )) {
        if (Test-Path $root -PathType Container) {
            Get-ChildItem $root -Recurse -File |
                Where-Object { $_.Extension.ToLowerInvariant() -in @('.appx', '.msix', '.appxbundle', '.msixbundle') } |
                ForEach-Object { $packages.Add($_.FullName) }
        }
    }
}

foreach ($package in $packages | Select-Object -Unique) {
    $stream = [System.IO.File]::OpenRead($package)
    try {
        Test-Archive -Stream $stream -Label $package
    }
    finally {
        $stream.Dispose()
    }
}

if ($violations.Count -gt 0) {
    Write-Error (
        "Store driver boundary violated:`n - " +
        (($violations | Sort-Object -Unique) -join "`n - "))
    exit 1
}

$packageMessage = if ($packages.Count -eq 0) {
    'No built package found; authored MSIX inputs are clean.'
}
else {
    "$($packages.Count) package(s) inspected."
}

Write-Host "Store driver boundary OK. $packageMessage"
