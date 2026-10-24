$ErrorActionPreference = 'Stop'
foreach ($name in @('matrix.stdout.txt', 'matrix.stderr.txt', 'matrix.sql')) {
    $path = Join-Path $PSScriptRoot $name
    $item = Get-Item -LiteralPath $path
    $hash = [Security.Cryptography.SHA256]::Create()
    $digest = ([BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($path)))).Replace('-', '').ToLowerInvariant()
    "$name bytes=$($item.Length) modifiedUtc=$($item.LastWriteTimeUtc.ToString('o')) sha256=$digest"
}
