# Generates PlayFairConstants.cs from doubletake's playfair_tables_compact.go
$src = Get-Content "D:\WinStream\tmp\WinStreamAirPlay2Harness\tmp\doubletake\internal\airplay\playfair_tables_compact.go" -Raw

function Extract-B64($name) {
    # Match: const name = "..." + "..." + ""
    if ($src -match "(?s)const\s+${name}\s*=\s*((?:`"[^`"]*`"\s*\+?\s*)+)") {
        $block = $Matches[1]
        $parts = [regex]::Matches($block, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
        $result = ($parts -join '')
        return $result
    }
    return ''
}

$names = @('messageKeyB64','messageIvB64','zKeyB64','xKeyB64','tKeyB64',
    's1BasesB64','s1DefsB64','s2BasesB64','s2DefsB64','s4BasesB64','s4DefsB64',
    's10BasesB64','s10DefsB64','tableS3B64','s5BasePermB64','s58MapsB64','s9MapsB64')

$lines = @()
$lines += '// Auto-generated from doubletake playfair_tables_compact.go - DO NOT EDIT'
$lines += 'namespace WinStream.Network'
$lines += '{'
$lines += '    internal static class PlayFairConstants'
$lines += '    {'

foreach ($n in $names) {
    $val = Extract-B64 $n
    Write-Host "$n => length $($val.Length)"
    # Split long strings across multiple lines for readability
    if ($val.Length -gt 120) {
        $lines += "        internal const string $n ="
        for ($i = 0; $i -lt $val.Length; $i += 100) {
            $chunk = $val.Substring($i, [Math]::Min(100, $val.Length - $i))
            if ($i + 100 -ge $val.Length) {
                $lines += "            `"$chunk`";"
            } else {
                $lines += "            `"$chunk`" +"
            }
        }
    } else {
        $lines += "        internal const string $n = `"$val`";"
    }
}

# Extract byte arrays from playfair.go
$pfSrc = Get-Content "D:\WinStream\tmp\WinStreamAirPlay2Harness\tmp\doubletake\internal\airplay\playfair.go" -Raw
$staticArrays = @('sapIV','sapKeyMaterial','indexMangle','initialSessionKey','staticSource1','staticSource2','defaultSap')
foreach ($arrName in $staticArrays) {
    $matched = $false
    foreach ($s in @($pfSrc, $src)) {
        if ($s -match "(?s)var\s+${arrName}\s*=\s*\[\d+\]byte\{(.*?)\}") {
            $hexVals = $Matches[1].Trim()
            $lines += ""
            $lines += "        internal static readonly byte[] $arrName = new byte[] { $hexVals };"
            $matched = $true
            break
        }
    }
    if (-not $matched) { Write-Host "WARNING: $arrName not found" }
}

$lines += '    }'
$lines += '}'

$lines -join "`r`n" | Set-Content "D:\WinStream\WinStream\Network\PlayFairConstants.cs" -Encoding UTF8
Write-Host "Done - Generated PlayFairConstants.cs"
