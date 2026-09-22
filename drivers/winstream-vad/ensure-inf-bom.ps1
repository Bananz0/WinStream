# StampInf often emits UTF-16 LE without a BOM; Inf2Cat then fails signability
# with "No installation INF found". Rewrite any .inf under -Path with a BOM.
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$Path = $Path.Trim().TrimEnd('\', '/')
$utf16 = New-Object System.Text.UnicodeEncoding $false, $true
Get-ChildItem -LiteralPath $Path -Filter *.inf -File -ErrorAction Stop | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText($_.FullName, $text, $utf16)
}
