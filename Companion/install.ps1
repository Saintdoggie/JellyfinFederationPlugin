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

function Stop-RunningCompanion {
    Write-Host "Stopping Companion and its media helper so files can be replaced..."
    foreach ($name in @("FederationCompanion", "rclone")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        & taskkill.exe /F /IM "$name.exe" 2>$null | Out-Null
    }
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

Write-Host "Installed to $installDir"
Write-Host "Starting Federation Companion - open the URL it prints. The media folder starts by itself."
Set-Location $installDir
& .\FederationCompanion.exe
