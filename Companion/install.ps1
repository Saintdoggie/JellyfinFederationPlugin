# One-line installer for Federation Companion (Windows).
#
#   irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
#
# Downloads the self-contained win-x64 build from the "companion-latest"
# GitHub release, extracts it, and launches it - no .NET install required,
# since the build already bundles its own runtime.

$ErrorActionPreference = "Stop"

$repo = "Saintdoggie/JellyfinFederationPlugin"
$tag = "companion-latest"
$installDir = if ($env:FEDERATION_COMPANION_DIR) { $env:FEDERATION_COMPANION_DIR } else { Join-Path $env:USERPROFILE "FederationCompanion" }
$url = "https://github.com/$repo/releases/download/$tag/FederationCompanion-win-x64.zip"
$archive = Join-Path $env:TEMP "federation-companion.zip"

Write-Host "Downloading Federation Companion (win-x64)..."
Invoke-WebRequest -Uri $url -OutFile $archive

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Expand-Archive -Path $archive -DestinationPath $installDir -Force
Remove-Item $archive

Write-Host "Installed to $installDir"
Write-Host "Starting Federation Companion - open the URL it prints in your browser."
Set-Location $installDir
& .\FederationCompanion.exe
