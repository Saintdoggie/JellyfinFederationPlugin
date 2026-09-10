# One-line installer for Federation Companion (Windows).
#
#   irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
#
# Downloads and verifies Companion, creates shortcuts, and opens setup.
# The app explains optional media-driver permissions before installation.

$ErrorActionPreference = "Stop"

$repo = "Saintdoggie/JellyfinFederationPlugin"
$tag = "companion-latest"
$installDir = if ($env:FEDERATION_COMPANION_DIR) { $env:FEDERATION_COMPANION_DIR } else { Join-Path $env:USERPROFILE "FederationCompanion" }
$url = "https://github.com/$repo/releases/download/$tag/FederationCompanion-win-x64.zip"
$archive = Join-Path $env:TEMP "federation-companion.zip"
Write-Host "Companion runs as your user, opens a local dashboard, and saves credentials/logs in $installDir."
Write-Host "Media mounting is optional: enable it in the dashboard after reviewing rclone, disk-cache and WinFsp permissions."
Write-Host "No automatic startup or public access is enabled by this installer."
Write-Host "Unsigned build: SmartScreen may warn. Investigate Defender detections; do not disable protection or add blanket exclusions."

function Stop-RunningCompanion {
    $expectedExe = [IO.Path]::GetFullPath((Join-Path $installDir "FederationCompanion.exe"))
    Get-Process -Name FederationCompanion -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path -and [string]::Equals($_.Path, $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -ErrorAction Stop
        }
    }
    $identityFile = Join-Path $installDir "media-mount.pid.identity"
    if (Test-Path -LiteralPath $identityFile) {
        $identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json
        $helper = Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
        if ($helper -and $helper.Name -eq 'rclone' -and
            $helper.StartTime.ToUniversalTime().Ticks -eq $identity.StartTimeUtcTicks -and
            [string]::Equals($helper.Path, $identity.Executable, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $helper.Id -ErrorAction Stop
        }
    }
}

$work = Join-Path $env:TEMP ("federation-companion-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
$archive = Join-Path $work "FederationCompanion-win-x64.zip"
try {
    Write-Host "Downloading Federation Companion (win-x64)..."
    Invoke-WebRequest -Uri $url -OutFile $archive
    $sums = (Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/SHA256SUMS" -UseBasicParsing).Content
    $line = ($sums -split "`n" | Where-Object { $_ -match '^[a-fA-F0-9]{64}  FederationCompanion-win-x64\.zip\s*$' })
    if (-not $line -or $line.Count -gt 1) { throw "Release checksum is missing or ambiguous." }
    $expected = ($line -split '\s+')[0]
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw "Companion checksum mismatch. Nothing installed." }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Stop-RunningCompanion
    Start-Sleep -Seconds 2
    Expand-Archive -LiteralPath $archive -DestinationPath $installDir -Force
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}

function New-CompanionShortcut {
    param([string]$Path, [string]$Arguments)
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $exe
        $shortcut.Arguments = $Arguments
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = "Federation Companion - Plex and Jellyfin federation"
        $shortcut.IconLocation = "$exe,0"
        $shortcut.Save()
    } catch {
        Write-Warning "Could not create the shortcut at $Path"
    }
}

$exe = Join-Path $installDir "FederationCompanion.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "FederationCompanion.exe was not extracted to $installDir." }

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Federation Companion.lnk"
New-CompanionShortcut -Path $startMenu -Arguments "--open"
New-CompanionShortcut -Path (Join-Path ([Environment]::GetFolderPath("Desktop")) "Federation Companion.lnk") -Arguments "--open"

Write-Host "Installed to $installDir"
Write-Host ""
Write-Host "Starting Federation Companion. It stays in the notification area (tray) and opens the dashboard in your browser."
Write-Host "Right-click the tray icon to open the dashboard, copy the owner key, manage the media folder, or exit."
Start-Process -FilePath $exe -ArgumentList "--open" -WorkingDirectory $installDir
