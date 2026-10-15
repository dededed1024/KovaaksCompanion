# Fetches the pinned OBS Studio Windows x64 release (zip SHA-256 verified) and packs the parts libobs needs for capture/encoding
# into src/KovaaksCompanion.App/libobs/libobs.zip, which the App csproj embeds. No-op when the pack exists for the same OBS zip.
# -Zip uses an already downloaded OBS zip (still hash-checked) instead of downloading.
param([switch]$Force, [string]$Zip = '')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '32.2.2'
$url = "https://github.com/obsproject/obs-studio/releases/download/$version/OBS-Studio-$version-Windows-x64.zip"
$zipSha = '4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1'
$dest = Join-Path $PSScriptRoot '..\src\KovaaksCompanion.App\libobs'
$pack = Join-Path $dest 'libobs.zip'
$marker = Join-Path $dest 'libobs.source'
if ((Test-Path $pack) -and (Test-Path $marker) -and -not $Force) {
    if ((Get-Content $marker -Raw).Trim() -eq "$version $zipSha") { Write-Host "libobs pack already present: $pack"; return }
    Write-Host 'libobs pack is for another OBS build, rebuilding'
}

# Entries kept from the OBS zip: core, the D3D11 graphics module, FFmpeg libs the plugins link, the GPU probes the encoder plugins launch,
# the effect files, and only the plugins used (window capture, WASAPI, DirectShow, NVENC, QSV, AMF + AAC in obs-ffmpeg).
$keep = @(
    '^bin/64bit/(avcodec-62|avdevice-62|avfilter-11|avformat-62|avutil-60|libcurl|libobs-d3d11|libobs-winrt|librist|libx264-164|obs|srt|swresample-6|swscale-9|w32-pthreads|zlib)\.dll$',
    '^bin/64bit/obs-(amf|nvenc|qsv)-test\.exe$',
    '^data/obs-plugins/win-capture/get-graphics-offsets(32|64)\.exe$',
    '^data/libobs/[^/]+\.effect$',
    '^obs-plugins/64bit/(obs-ffmpeg|obs-nvenc|obs-qsv11|win-capture|win-dshow|win-wasapi)\.dll$'
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('kc-libobs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    if ($Zip) { $src = $Zip } else {
        $src = Join-Path $tmp 'obs.zip'
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $src -UseBasicParsing
    }
    $actual = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $zipSha) { throw "OBS zip SHA-256 mismatch: expected $zipSha, got $actual" }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $out = Join-Path $tmp 'libobs.zip'
    $in = [IO.Compression.ZipFile]::OpenRead($src)
    $zo = [IO.Compression.ZipFile]::Open($out, 'Create')
    try {
        $n = 0
        foreach ($e in $in.Entries) {
            if (-not ($keep | Where-Object { $e.FullName -match $_ })) { continue }
            $ne = $zo.CreateEntry($e.FullName, [IO.Compression.CompressionLevel]::Optimal)
            $ne.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)  # same input -> same bytes -> same runtime folder key
            $s = $e.Open(); $d = $ne.Open()
            try { $s.CopyTo($d) } finally { $d.Dispose(); $s.Dispose() }
            $n++
        }
        if ($n -lt 40) { throw "only $n entries matched, OBS layout changed?" }
    } finally { $zo.Dispose(); $in.Dispose() }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Move-Item $out $pack -Force
    Set-Content $marker "$version $zipSha"
    Write-Host ("Wrote {0} ({1:N1} MB)" -f $pack, ((Get-Item $pack).Length / 1MB))
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
