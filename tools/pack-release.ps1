param(
    [string]$Configuration = "Release",
    [string]$Version = (Get-Date -Format "yyyy.MM.dd")
)

# Builds a distributable zip. The Google OAuth client is included only if google-client.json exists in the repo
# root (it is git-ignored). yt-dlp is bundled when a local copy can be found, so end users do not have to install it.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\YomuPlayer.Desktop\YomuPlayer.Desktop.csproj"
$output = Join-Path $root "src\YomuPlayer.Desktop\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64"
$distRoot = Join-Path $root "dist"
$dist = Join-Path $distRoot "YOMU-Player-$Version-win-x64"
$zip = Join-Path $distRoot "YOMU-Player-$Version-win-x64.zip"

Write-Host "Building $Configuration..."
dotnet build $project -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$client = Join-Path $root "google-client.json"
$outputClient = Join-Path $output "google-client.json"
if (!(Test-Path $client)) {
    # A previous build may have copied a client into bin. Never ship a stale one.
    Remove-Item $outputClient -Force -ErrorAction SilentlyContinue
    Write-Warning "google-client.json not found in the repo root. Users will be asked for an OAuth client on first run."
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
Copy-Item (Join-Path $output "*") $dist -Recurse -Force

if (Test-Path $client) {
    Copy-Item $client (Join-Path $dist "google-client.json") -Force
    Write-Host "Bundled google-client.json (users will not see the setup dialog)."
}

$ytDlpCandidates = @(
    (Join-Path $env:LOCALAPPDATA "YomuPlayer\yt-dlp\yt-dlp.exe"),
    (Join-Path $root "tools\yt-dlp.exe"),
    (Get-Command yt-dlp -ErrorAction SilentlyContinue | Select-Object -First 1).Source
)

$ytDlp = $ytDlpCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($ytDlp) {
    Copy-Item $ytDlp (Join-Path $dist "yt-dlp.exe") -Force
    Write-Host "Bundled yt-dlp: $ytDlp"
}
else {
    Write-Warning "yt-dlp.exe not found. Users will need yt-dlp on PATH for playback."
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-FileHash $zip -Algorithm SHA256
"$($hash.Hash)  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ASCII

Write-Host ""
Write-Host "Release: $zip"
Write-Host "SHA256:  $($hash.Hash)"
Write-Host "Folder:  $dist"
