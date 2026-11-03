$ErrorActionPreference = "Stop"
$root = "C:\YomuPlayer"
$z = Get-ChildItem "$HOME\Downloads\YOMU-Player*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Using $($z.Name) from $($z.LastWriteTime)"
$tmp = Join-Path $env:TEMP "yomu_update"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $z.FullName -DestinationPath $tmp
$now = Get-Date
Get-ChildItem $tmp -Recurse -File | ForEach-Object { $_.LastWriteTime = $now }
robocopy $tmp $root /E /IS /IT /NFL /NDL /NJH /NJS | Out-Null
Get-ChildItem "$root\src" -Recurse -Directory -Include bin,obj -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Updated and cleared build output."
