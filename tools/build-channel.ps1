param(
    [ValidateSet('stable', 'nightly')]
    [string]$Channel = 'stable',
    [string]$Version = '',
    [string]$FileVersion = '',
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$ReleaseNotes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\YomuPlayer.Desktop\YomuPlayer.Desktop.csproj'
$inno = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Version) {
    $Version = if ($Channel -eq 'nightly') {
        '1.0.0-nightly.' + (Get-Date -Format 'yyyyMMdd.HHmm')
    } else {
        '1.0.0'
    }
}
if (-not $FileVersion) {
    if ($Version -match '^(\d+)\.(\d+)\.(\d+)') {
        $FileVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    } else {
        $FileVersion = '1.0.0.0'
    }
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'dist' }
if (-not $ReleaseNotes) { $ReleaseNotes = "YOMU Player $Version" }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$icon = if ($Channel -eq 'nightly') {
    Join-Path $root 'src\YomuPlayer.Desktop\Assets\app-nightly.ico'
} else {
    Join-Path $root 'src\YomuPlayer.Desktop\Assets\app.ico'
}
$smallIcon = if ($Channel -eq 'nightly') {
    Join-Path $root 'src\YomuPlayer.Desktop\Assets\app-nightly-small.bmp'
} else {
    Join-Path $root 'src\YomuPlayer.Desktop\Assets\app-small.bmp'
}

Write-Host "Building $Channel $Version..."
$buildArgs = @(
    'build', $project, '-c', $Configuration, '-v', 'minimal',
    "-p:Version=$Version",
    "-p:FileVersion=$FileVersion",
    "-p:InformationalVersion=$Version"
)
if ($Channel -eq 'nightly') {
    $buildArgs += "-p:ApplicationIcon=$icon"
}
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$raw = Join-Path $root "src\YomuPlayer.Desktop\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path (Join-Path $raw 'YomuPlayer.exe'))) { throw "Build output missing: $raw" }

$stageRoot = Join-Path $OutputRoot "stage-$Channel"
Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
Copy-Item (Join-Path $raw '*') $stageRoot -Recurse -Force
Remove-Item (Join-Path $stageRoot 'data') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stageRoot 'portable.marker') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stageRoot 'installed.marker') -Force -ErrorAction SilentlyContinue

$manifestUrl = if ($Channel -eq 'nightly') {
    'https://kabouls.github.io/YOMU-Player/nightly.json'
} else {
    'https://kabouls.github.io/YOMU-Player/stable.json'
}
@{ manifestUrl = $manifestUrl } | ConvertTo-Json | Set-Content (Join-Path $stageRoot 'update-source.json') -Encoding UTF8

if ($Channel -eq 'nightly') {
    Set-Content (Join-Path $stageRoot 'channel.txt') 'nightly' -Encoding ASCII
} else {
    Remove-Item (Join-Path $stageRoot 'channel.txt') -Force -ErrorAction SilentlyContinue
}

# Keep the bundled changelog in step with the version being packaged.
$changelogPath = Join-Path $stageRoot 'changelog.json'
$entries = @()
if (Test-Path $changelogPath) {
    try {
        $parsed = Get-Content $changelogPath -Raw | ConvertFrom-Json
        if ($parsed -is [System.Array]) {
            foreach ($item in $parsed) { $entries += $item }
        } elseif ($null -ne $parsed) {
            $entries = @($parsed)
        }
    } catch { $entries = @() }
}
if (-not ($entries | Where-Object { $_.version -eq $Version })) {
    $noteLines = @($ReleaseNotes -split "\r?\n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and $_ -notmatch '^[-*]\s*$' })
    if ($noteLines.Count -eq 0) { $noteLines = @("YOMU Player $Version") }
    $entry = [ordered]@{
        version = $Version
        date = (Get-Date -Format 'yyyy-MM-dd')
        channel = $Channel
        notes = $noteLines
    }
    $entries = @($entry) + $entries
}
ConvertTo-Json -InputObject @($entries) -Depth 6 | Set-Content $changelogPath -Encoding UTF8

# Bundle yt-dlp if available locally.
$ytDlp = @(
    (Join-Path $env:LOCALAPPDATA 'YomuPlayer\yt-dlp\yt-dlp.exe'),
    (Join-Path $root 'tools\yt-dlp.exe'),
    (Get-Command yt-dlp -ErrorAction SilentlyContinue | Select-Object -First 1).Source
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($ytDlp) {
    Copy-Item $ytDlp (Join-Path $stageRoot 'yt-dlp.exe') -Force
}

# Portable package: files at ZIP root + portable marker.
$portableDir = Join-Path $OutputRoot "portable-$Channel"
Remove-Item $portableDir -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $stageRoot $portableDir -Recurse -Force
New-Item -ItemType File -Path (Join-Path $portableDir 'portable.marker') -Force | Out-Null
$portableZip = Join-Path $OutputRoot ("YOMU-Player-{0}-Portable-{1}.zip" -f ($(if ($Channel -eq 'nightly') { 'Nightly' } else { '' })).Trim('-'), $Version)
$portableZip = $portableZip -replace '--', '-'
Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $portableZip -CompressionLevel Optimal

# Installer package: installed marker, no portable marker.
$installerDir = Join-Path $OutputRoot "installer-$Channel"
Remove-Item $installerDir -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $stageRoot $installerDir -Recurse -Force
New-Item -ItemType File -Path (Join-Path $installerDir 'installed.marker') -Force | Out-Null

if (-not $inno) {
    Write-Warning "Inno Setup not found; installer was not built."
    $setupExe = $null
} else {
    $innoArgs = @(
        "/DChannel=$Channel",
        "/DAppVersion=$Version",
        "/DFileVersion=$FileVersion",
        "/DSourceDir=$installerDir",
        "/DOutputDir=$OutputRoot",
        "/DSetupIcon=$icon",
        "/DSetupSmallImage=$smallIcon"
    )
    if ($Channel -eq 'nightly') { $innoArgs += '/DNightly=1' }
    $innoArgs += (Join-Path $root 'installer\YOMU-Player.iss')
    & $inno @innoArgs
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    $setupExe = Get-ChildItem $OutputRoot -Filter "YOMU-Player-Setup-$Version.exe" -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setupExe) {
        $setupExe = Get-ChildItem $OutputRoot -Filter "YOMU-Player-Nightly-Setup-$Version.exe" -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
}

$result = [ordered]@{
    channel = $Channel
    version = $Version
    fileVersion = $FileVersion
    setup = if ($setupExe) { [ordered]@{ path = $setupExe.FullName; sha256 = (Get-FileHash $setupExe.FullName -Algorithm SHA256).Hash } } else { $null }
    portable = [ordered]@{ path = $portableZip; sha256 = (Get-FileHash $portableZip -Algorithm SHA256).Hash }
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputRoot "release-$Channel.json") -Encoding UTF8
Write-Host "Built:"
$result | ConvertTo-Json -Depth 6
