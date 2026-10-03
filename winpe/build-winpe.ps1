# Builds a WinPE ISO with the Usenet Backup recovery tools.
# Run on Windows with the ADK + WinPE add-on installed, as Administrator.
#
#   1. Install ADK (Deployment Tools) + WinPE add-on from
#      https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install/
#   2. dotnet publish the CLI and wizard (self-contained, win-x64)
#   3. Run this script: .\winpe\build-winpe.ps1 -SourceDir C:\ub\src
#
# Output: C:\winpe\usenet-backup-winpe.iso

param(
    [string]$SourceDir = "C:\ub\src",
    [string]$WorkDir = "C:\winpe",
    [string]$IsoPath = "C:\winpe\usenet-backup-winpe.iso"
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Error $msg; exit 1 }

# --- Preconditions ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run as Administrator."
}
foreach ($tool in @("copype.cmd", "MakeWinPEMedia.cmd")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool not found. Install ADK Deployment Tools + WinPE add-on." }
}
if (-not (Get-Command dism.exe -ErrorAction SilentlyContinue)) { Fail "dism.exe not found." }

# --- Publish self-contained binaries ---
Write-Host "Publishing CLI and recovery wizard (self-contained win-x64)..."
$cliOut = Join-Path $WorkDir "publish\cli"
$wizOut = Join-Path $WorkDir "publish\recovery"
dotnet publish (Join-Path $SourceDir "src\UsenetBackup.Cli\UsenetBackup.Cli.csproj") `
    -c Release -r win-x64 --self-contained -o $cliOut
if ($LASTEXITCODE -ne 0) { Fail "CLI publish failed." }
dotnet publish (Join-Path $SourceDir "src\UsenetBackup.Recovery\UsenetBackup.Recovery.csproj") `
    -c Release -r win-x64 --self-contained -o $wizOut
if ($LASTEXITCODE -ne 0) { Fail "Wizard publish failed." }

# --- Build WinPE base ---
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
Write-Host "Running copype..."
copype.cmd amd64 $WorkDir
if ($LASTEXITCODE -ne 0) { Fail "copype failed." }

$mountDir = Join-Path $WorkDir "mount"
$bootWim = Join-Path $WorkDir "media\sources\boot.wim"

Write-Host "Mounting boot.wim..."
dism.exe /Mount-Image /ImageFile:$bootWim /index:1 /MountDir:$mountDir
if ($LASTEXITCODE -ne 0) { Fail "DISM mount failed." }

try {
    # --- Add recovery tools ---
    $toolsDir = Join-Path $mountDir "usenet-backup"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Write-Host "Copying CLI..."
    Copy-Item (Join-Path $cliOut "*") $toolsDir -Recurse -Force
    $wizardDir = Join-Path $toolsDir "wizard"
    New-Item -ItemType Directory -Force -Path $wizardDir | Out-Null
    Write-Host "Copying wizard..."
    Copy-Item (Join-Path $wizOut "*") $wizardDir -Recurse -Force
    Write-Host "Copying recovery runbook..."
    Copy-Item (Join-Path $SourceDir "docs\RECOVERY.md") $toolsDir -Force

    # --- Startup script: network init + menu ---
    $startnet = @'
wpeinit
@echo off
echo ================================================
echo  Usenet Backup Recovery Environment
echo ================================================
echo.
echo  Tools in X:\usenet-backup\ :
echo    usenet-backup.exe           (CLI)
echo    wizard\usenet-backup-recovery.exe  (GUI wizard)
echo    RECOVERY.md                 (runbook)
echo.
echo  Start networking if needed: wpeinit
echo  Launch the wizard: X:\usenet-backup\wizard\usenet-backup-recovery.exe
echo.
cmd
'@
    $startnet | Out-File -FilePath (Join-Path $mountDir "Windows\System32\startnet.cmd") `
        -Encoding ascii -Force
    Write-Host "startnet.cmd written."
}
finally {
    Write-Host "Committing and unmounting..."
    dism.exe /Unmount-Image /MountDir:$mountDir /Commit
    if ($LASTEXITCODE -ne 0) { Fail "DISM unmount failed." }
}

# --- Build ISO ---
Write-Host "Building ISO..."
MakeWinPEMedia.cmd /ISO $WorkDir $IsoPath
if ($LASTEXITCODE -ne 0) { Fail "MakeWinPEMedia failed." }

Write-Host ""
Write-Host "Done: $IsoPath"
Write-Host "Write it to USB with Rufus (or MakeWinPEMedia /UFD)."
