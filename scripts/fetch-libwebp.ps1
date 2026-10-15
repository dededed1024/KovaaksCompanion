# Fetches the pinned libwebp Windows x64 release (zip SHA-256 verified) and keeps only bin/img2webp.exe (animated WebP writer)
# in src/KovaaksCompanion.App/libwebp/, which the App csproj embeds. No-op when it is already there for the same zip.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '1.5.0'
$url = "https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-$version-windows-x64.zip"
$zipSha = 'e8fe3bc7eb09774e69261a42bf9fa8a37ab5f3eecaab199f6420e6f9e822090c'
$dest = Join-Path $PSScriptRoot '..\src\KovaaksCompanion.App\libwebp'
$exe = Join-Path $dest 'img2webp.exe'
$marker = Join-Path $dest 'libwebp.source'
if ((Test-Path $exe) -and (Test-Path $marker) -and -not $Force) {
    if ((Get-Content $marker -Raw).Trim() -eq "$version $zipSha") { Write-Host "img2webp already present: $exe"; return }
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('kc-libwebp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $src = Join-Path $tmp 'webp.zip'
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $src -UseBasicParsing
    $actual = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $zipSha) { throw "libwebp zip SHA-256 mismatch: expected $zipSha, got $actual" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $in = [IO.Compression.ZipFile]::OpenRead($src)
    try {
        $e = $in.Entries | Where-Object { $_.FullName -eq "libwebp-$version-windows-x64/bin/img2webp.exe" }
        if (-not $e) { throw 'img2webp.exe not found in the libwebp zip' }
        New-Item -ItemType Directory -Force $dest | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $exe, $true)
    } finally { $in.Dispose() }
    Set-Content $marker "$version $zipSha"
    Write-Host "Wrote $exe"
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
