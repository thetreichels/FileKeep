# Builds a WinPE ISO with the FileKeep recovery tools.
# Run on Windows with the ADK + WinPE add-on installed, as Administrator.
#
# Two modes:
#   1. Source mode (dev): publishes from a source tree.
#      .\winpe\build-winpe.ps1 -SourceDir C:\ub\src
#   2. Binary mode (installed): uses pre-published binaries, e.g. from
#      C:\Program Files\FileKeep. The dashboard's WinPE GUI uses this mode.
#      .\winpe\build-winpe.ps1 -BinaryDir "C:\Program Files\FileKeep"
#
# In binary mode the layout is expected to be:
#   <BinaryDir>\cli\FileKeep.exe
#   <BinaryDir>\wizard\FileKeepRecovery.exe
#   <BinaryDir>\docs\RECOVERY.md
#
# Output: C:\winpe\filekeep-winpe.iso

param(
    [string]$SourceDir = "C:\ub\src",
    [string]$BinaryDir = "",
    [string]$WorkDir = "C:\winpe",
    [string]$StageDir = "C:\winpe-stage",
    [string]$IsoPath = "C:\winpe\filekeep-winpe.iso"
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
$cliOut = Join-Path $StageDir "cli"
$wizOut = Join-Path $StageDir "recovery"
if ($BinaryDir -ne "") {
    # Binary mode: copy pre-published binaries from the installed layout.
    # No source tree or dotnet SDK required.
    Write-Host "Binary mode: copying pre-published binaries from $BinaryDir..."
    $srcCli = Join-Path $BinaryDir "cli\FileKeep.exe"
    $srcWiz = Join-Path $BinaryDir "wizard\FileKeepRecovery.exe"
    if (-not (Test-Path $srcCli)) { Fail "CLI not found: $srcCli" }
    if (-not (Test-Path $srcWiz)) { Fail "Wizard not found: $srcWiz" }
    New-Item -ItemType Directory -Force -Path $cliOut | Out-Null
    New-Item -ItemType Directory -Force -Path $wizOut | Out-Null
    Copy-Item (Join-Path $BinaryDir "cli\*") $cliOut -Recurse -Force
    Copy-Item (Join-Path $BinaryDir "wizard\*") $wizOut -Recurse -Force
}
else {
    Write-Host "Publishing CLI and recovery wizard (self-contained win-x64)..."
    dotnet publish (Join-Path $SourceDir "src\UsenetBackup.Cli\UsenetBackup.Cli.csproj") `
        -c Release -r win-x64 --self-contained -o $cliOut
    if ($LASTEXITCODE -ne 0) { Fail "CLI publish failed." }
    dotnet publish (Join-Path $SourceDir "src\UsenetBackup.Recovery\UsenetBackup.Recovery.csproj") `
        -c Release -r win-x64 --self-contained -o $wizOut
    if ($LASTEXITCODE -ne 0) { Fail "Wizard publish failed." }
}

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
    $toolsDir = Join-Path $mountDir "filekeep"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Write-Host "Copying CLI..."
    Copy-Item (Join-Path $cliOut "*") $toolsDir -Recurse -Force
    $wizardDir = Join-Path $toolsDir "wizard"
    New-Item -ItemType Directory -Force -Path $wizardDir | Out-Null
    Write-Host "Copying wizard..."
    Copy-Item (Join-Path $wizOut "*") $wizardDir -Recurse -Force
    Write-Host "Copying recovery runbook..."
    $runbookSrc = if ($BinaryDir -ne "") { Join-Path $BinaryDir "docs\RECOVERY.md" } `
                  else { Join-Path $SourceDir "docs\RECOVERY.md" }
    if (Test-Path $runbookSrc) {
        Copy-Item $runbookSrc $toolsDir -Force
    }
    else {
        Write-Host "WARNING: RECOVERY.md not found at $runbookSrc; skipping."
    }

    # --- Startup script: network init + menu ---
    $startnet = @'
wpeinit
@echo off
echo ================================================
echo  Usenet Backup Recovery Environment
echo ================================================
echo.
echo  Tools in X:\filekeep\ :
echo    FileKeep.exe                   (CLI)
echo    wizard\FileKeepRecovery.exe    (GUI wizard)
echo    RECOVERY.md                    (runbook)
echo.
echo  Start networking if needed: wpeinit
echo  Launching recovery wizard...
start "" X:\filekeep\wizard\FileKeepRecovery.exe --win95
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
