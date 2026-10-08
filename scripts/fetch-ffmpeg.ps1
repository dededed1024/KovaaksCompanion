# Fetches the pinned BtbN ffmpeg n9.0.2 LGPL build (no GPL/nonfree libs, no libx264) (zip + exe SHA-256 verified) and extracts bin/ffmpeg.exe
# to src/KovaaksCompanion.App/ffmpeg/ffmpeg.exe, where the App csproj embeds it. No-op when the file exists and matches.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-07-13-07/ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-9.0.zip'
$zipSha = '3aa4a8161a29866fba8f8c87f07dc0a57c4c9bab42c0db84510b046ca80d41cd'
$exeSha = 'c43e397fdf3303d04172122f5402cac66bdbb24e6727184f22c2070e2dbee7d0'
$dest = Join-Path $PSScriptRoot '..\src\KovaaksCompanion.App\ffmpeg'
$exe = Join-Path $dest 'ffmpeg.exe'
if ((Test-Path $exe) -and -not $Force) {
    if ((Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $exeSha) { Write-Host "ffmpeg already present: $exe"; return }
    Write-Host 'ffmpeg.exe hash mismatch, re-fetching'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('kc-ffmpeg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $zipSha) { throw "zip SHA-256 mismatch: expected $zipSha, got $actual" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $za.Entries | Where-Object { $_.FullName -match '(^|/)bin/ffmpeg\.exe$' } | Select-Object -First 1
        if (-not $entry) { throw 'bin/ffmpeg.exe not found in archive' }
        $out = Join-Path $tmp 'ffmpeg.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
    } finally { $za.Dispose() }
    $actual = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $exeSha) { throw "ffmpeg.exe SHA-256 mismatch: expected $exeSha, got $actual" }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Move-Item $out $exe -Force
    Write-Host ("Wrote {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
