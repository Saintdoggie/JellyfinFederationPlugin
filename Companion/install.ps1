# One-line installer for Federation Companion (Windows).
#
#   irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
#
# Downloads Companion, installs the Windows media driver if needed, and starts
# Companion. The media folder starts by itself — no rclone install, no extra
# setup after this script.

$ErrorActionPreference = "Stop"

$repo = "Saintdoggie/JellyfinFederationPlugin"
$tag = "companion-latest"
$installDir = if ($env:FEDERATION_COMPANION_DIR) { $env:FEDERATION_COMPANION_DIR } else { Join-Path $env:USERPROFILE "FederationCompanion" }
$url = "https://github.com/$repo/releases/download/$tag/FederationCompanion-win-x64.zip"
$archive = Join-Path $env:TEMP "federation-companion.zip"
# Keep in sync with Companion/WinFspInstaller.cs (2.2B4; 2.1 has known local LPE CVEs).
$winfspUrl = "https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi"
$winfspSha = "2ECB5C89405488A95BBD8A01875E02C48534FD37BBDFD84488F7590464D65944"

function Test-WinFsp {
    $roots = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)})
    foreach ($root in $roots) {
        if (-not $root) { continue }
        foreach ($name in @("winfsp-x64.dll", "winfsp-a64.dll", "winfsp.dll")) {
            if (Test-Path (Join-Path $root "WinFsp\bin\$name")) { return $true }
        }
    }
    return $false
}

function Stop-OwnedRclone {
    $pidFile = Join-Path $installDir "media-mount.pid"
    if (Test-Path -LiteralPath $pidFile) {
        $ownedPid = (Get-Content -LiteralPath $pidFile -TotalCount 1 -ErrorAction SilentlyContinue)
        if ($ownedPid) { $ownedPid = $ownedPid.ToString().Trim() }
        if ($ownedPid -match '^[0-9]+$') {
            $proc = Get-Process -Id ([int]$ownedPid) -ErrorAction SilentlyContinue
            if ($proc -and $proc.Name -eq 'rclone') {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                & taskkill.exe /F /T /PID $proc.Id 2>$null | Out-Null
            }
        }
    }
    Get-Process -Name rclone -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        if ($path -and $path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Stop-RunningCompanion {
    Write-Host "Stopping Companion and its media helper so files can be replaced..."
    Get-Process -Name FederationCompanion -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    & taskkill.exe /F /IM FederationCompanion.exe 2>$null | Out-Null
    Stop-OwnedRclone
    Start-Sleep -Seconds 2
    $rclone = Join-Path $installDir "rclone.exe"
    if (Test-Path $rclone) {
        try {
            $stale = Join-Path $installDir ("rclone.exe.old-" + [guid]::NewGuid().ToString("N"))
            Move-Item -LiteralPath $rclone -Destination $stale -Force
            Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Host "rclone.exe is still closing..."
        }
    }
}

Write-Host "Downloading Federation Companion (win-x64)..."
Invoke-WebRequest -Uri $url -OutFile $archive

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Stop-RunningCompanion
$extracted = $false
foreach ($attempt in 1..5) {
    try {
        Expand-Archive -Path $archive -DestinationPath $installDir -Force
        $extracted = $true
        break
    } catch {
        if ($attempt -eq 5) { throw }
        Write-Host "Waiting for rclone.exe to close, then retrying ($attempt/5)..."
        Stop-RunningCompanion
    }
}
if (-not $extracted) { throw "Could not replace Companion files. Close Companion and rclone, then run this again." }
Remove-Item $archive

if (-not (Test-WinFsp)) {
    Write-Host "Installing the Windows media driver (one-time). Windows may ask for permission..."
    $msi = Join-Path $env:TEMP "winfsp-2.2.26215.msi"
    try {
        Invoke-WebRequest -Uri $winfspUrl -OutFile $msi
        $actual = (Get-FileHash $msi -Algorithm SHA256).Hash
        if ($actual -ne $winfspSha) { throw "WinFsp download was corrupted." }
        Start-Process -FilePath msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart" -Verb RunAs -Wait | Out-Null
        if (-not (Test-WinFsp)) {
            Write-Warning "The media driver is not installed yet. Companion will ask Windows again when it starts."
        }
    } catch {
        Write-Warning "Could not install the Windows media driver automatically: $($_.Exception.Message)"
    }
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
Write-Host "About permissions: Companion is not code-signed, so Windows SmartScreen may say 'Windows protected your PC' - choose More info, then Run anyway."
Write-Host "It runs as your normal user, listens only on localhost, and only asks for a one-time UAC prompt to install the WinFsp media driver."
Write-Host "It writes companion-state.json (credentials) and companion.log in this folder. Keep the folder private."
Write-Host ""
Write-Host "Starting Federation Companion. It stays in the notification area (tray) and opens the dashboard in your browser."
Write-Host "Right-click the tray icon to open the dashboard, copy the owner key, manage the media folder, or exit."
Start-Process -FilePath $exe -ArgumentList "--open" -WorkingDirectory $installDir
