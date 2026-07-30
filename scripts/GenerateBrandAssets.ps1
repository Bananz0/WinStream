param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Add-Type -AssemblyName System.Drawing

$branding = Join-Path $RepositoryRoot "docs\branding"
$assets = Join-Path $RepositoryRoot "WinStream\Assets"
$logo = Join-Path $branding "winstream-logo-master.png"
$tray = Join-Path $branding "winstream-tray-icon.png"
$splash = Join-Path $branding "winstream-splash-wide.png"

foreach ($source in @($logo, $tray, $splash)) {
    if (-not (Test-Path $source)) {
        throw "Branding source not found: $source"
    }
}

New-Item -ItemType Directory -Force -Path $assets | Out-Null

function Export-Asset {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$Width,
        [int]$Height
    )

    $sourceImage = [System.Drawing.Image]::FromFile($Source)
    try {
        $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                $scale = [Math]::Max($Width / $sourceImage.Width, $Height / $sourceImage.Height)
                $drawWidth = [int][Math]::Ceiling($sourceImage.Width * $scale)
                $drawHeight = [int][Math]::Ceiling($sourceImage.Height * $scale)
                $x = [int](($Width - $drawWidth) / 2)
                $y = [int](($Height - $drawHeight) / 2)
                $graphics.DrawImage($sourceImage, $x, $y, $drawWidth, $drawHeight)
            }
            finally {
                $graphics.Dispose()
            }

            $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

$assetSpecs = @(
    @($logo, "StoreLogo.png", 50, 50),
    @($logo, "StoreLogo.scale-200.png", 100, 100),
    @($logo, "Square44x44Logo.scale-200.png", 88, 88),
    @($logo, "Square150x150Logo.scale-200.png", 300, 300),
    @($logo, "LockScreenLogo.scale-200.png", 48, 48),
    @($splash, "Wide310x150Logo.scale-200.png", 620, 300),
    @($splash, "SplashScreen.scale-200.png", 1240, 600),
    @($tray, "Square44x44Logo.targetsize-16_altform-unplated.png", 16, 16),
    @($tray, "Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24),
    @($tray, "Square44x44Logo.targetsize-32_altform-unplated.png", 32, 32),
    @($tray, "Square44x44Logo.targetsize-48_altform-unplated.png", 48, 48),
    @($tray, "Square44x44Logo.targetsize-256_altform-unplated.png", 256, 256),
    @($tray, "WinStreamTray-16.png", 16, 16),
    @($tray, "WinStreamTray-32.png", 32, 32)
)

foreach ($spec in $assetSpecs) {
    Export-Asset `
        -Source $spec[0] `
        -Destination (Join-Path $assets $spec[1]) `
        -Width $spec[2] `
        -Height $spec[3]
}

$iconImages = @(
    @{ Size = 16; Bytes = [IO.File]::ReadAllBytes((Join-Path $assets "WinStreamTray-16.png")) },
    @{ Size = 32; Bytes = [IO.File]::ReadAllBytes((Join-Path $assets "WinStreamTray-32.png")) }
)

$iconPath = Join-Path $assets "WinStreamTray.ico"
$stream = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$iconImages.Count)

    $offset = 6 + (16 * $iconImages.Count)
    foreach ($image in $iconImages) {
        $writer.Write([byte]$image.Size)
        $writer.Write([byte]$image.Size)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $iconImages) {
        $writer.Write($image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output "Generated WinStream branding assets in $assets"
