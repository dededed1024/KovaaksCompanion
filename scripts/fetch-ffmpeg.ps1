# Fetches the pinned gyan.dev ffmpeg 9.0.2 essentials build (zip + exe SHA-256 verified) and extracts bin/ffmpeg.exe
# to src/KovaaksCompanion.App/ffmpeg/ffmpeg.exe, where the App csproj embeds it. No-op when the file exists and matches.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$url = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
$zipSha = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$exeSha = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec'
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
