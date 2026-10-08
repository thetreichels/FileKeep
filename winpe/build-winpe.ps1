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
    [string]$StageDir = "C:\winpe-stage",
    [string]$IsoPath = "C:\winpe\usenet-backup-winpe.iso"
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Error $msg; exit 1 }

# --- Preconditions ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run as Administrator."
}
# The ADK installer does not put Deployment Tools on PATH, and the tools are
# not always at the textbook location, so probe the standard kit dirs, then
# PATH, then search the kit tree (small) before giving up.
function Find-AdkTool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kitRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
        "C:\Program Files\Windows Kits\10\Assessment and Deployment Kit"
    )
    foreach ($root in $kitRoots) {
        $probe = Join-Path $root "Deployment Tools\$name"
        if (Test-Path $probe) { return $probe }
    }
    foreach ($root in $kitRoots) {
        if (Test-Path $root) {
            $found = Get-ChildItem $root -Filter $name -Recurse -ErrorAction SilentlyContinue |
                     Select-Object -First 1 -ExpandProperty FullName
            if ($found) { return $found }
        }
    }
    return $null
}
foreach ($tool in @("copype.cmd", "MakeWinPEMedia.cmd")) {
    $toolPath = Find-AdkTool $tool
    if (-not $toolPath) { Fail "$tool not found. Install ADK Deployment Tools + WinPE add-on." }
    $toolDir = Split-Path $toolPath
    if ($env:PATH -notlike "*$toolDir*") { $env:PATH = "$toolDir;$env:PATH" }
    Write-Host "$tool -> $toolPath"
}
if (-not (Get-Command dism.exe -ErrorAction SilentlyContinue)) { Fail "dism.exe not found." }

# --- Publish self-contained binaries ---
# NOTE: publish into StageDir, NOT WorkDir — WorkDir is wiped below by copype.
Write-Host "Publishing CLI and recovery wizard (self-contained win-x64)..."
$cliOut = Join-Path $StageDir "cli"
$wizOut = Join-Path $StageDir "recovery"
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
    # --- WinPE optional components ---
    # The wizard's drive picker uses WMI (System.Management); the base WinPE
    # image may not include it, so add the WinPE-WMI package when present.
    $ocDir = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs"
    $wmiCab = Join-Path $ocDir "WinPE-WMI.cab"
    $wmiLang = Join-Path $ocDir "en-us\WinPE-WMI_en-us.cab"
    if (Test-Path $wmiCab) {
        Write-Host "Adding WinPE-WMI optional component..."
        $wmiArgs = @("/Add-Package", "/Image:$mountDir", "/PackagePath:$wmiCab")
        if (Test-Path $wmiLang) { $wmiArgs += "/PackagePath:$wmiLang" }
        & dism.exe @wmiArgs
        if ($LASTEXITCODE -ne 0) { Fail "DISM add WinPE-WMI failed." }
    }
    else {
        Write-Host "WARNING: WinPE-WMI.cab not found; the wizard drive picker may not work in WinPE."
    }

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
echo    UsenetBackup.exe           (CLI)
echo    wizard\UsenetBackupRecovery.exe  (GUI wizard)
echo    RECOVERY.md                 (runbook)
echo.
echo  Start networking if needed: wpeinit
echo  Launching recovery wizard...
start "" X:\usenet-backup\wizard\UsenetBackupRecovery.exe --win95
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
