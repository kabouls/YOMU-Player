param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$OutputRoot = ""
)

# Kept for compatibility with older local workflows. The channel builder creates the setup EXE,
# portable ZIP, channel files, update source and release metadata in one pass.
$ErrorActionPreference = "Stop"
$buildArgs = @{
    Channel = "stable"
    Version = $Version
    Configuration = $Configuration
}
if ($OutputRoot) { $buildArgs.OutputRoot = $OutputRoot }
& (Join-Path $PSScriptRoot "build-channel.ps1") @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
