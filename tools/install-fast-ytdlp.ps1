# Installs the "onedir" yt-dlp build (starts faster than the single-file exe, which unpacks itself on every run).
$ErrorActionPreference = "Stop"
$dest = Join-Path $env:LOCALAPPDATA "YomuPlayer\yt-dlp"
$zip  = Join-Path $env:TEMP "yt-dlp_win.zip"
$url  = "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp_win.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Expand-Archive -Path $zip -DestinationPath $dest -Force
$exe = Get-ChildItem $dest -Recurse -Filter yt-dlp.exe | Select-Object -First 1
if (-not $exe) { throw "yt-dlp.exe not found in the archive" }
if ($exe.DirectoryName -ne $dest) { Copy-Item "$($exe.DirectoryName)\*" $dest -Recurse -Force }
& (Join-Path $dest "yt-dlp.exe") --version
Write-Host "Installed to $dest"
