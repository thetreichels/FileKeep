# Builds a bootable FileKeep recovery USB from the host's WinRE image.
# ADK-free: uses only in-box Windows components (reagentc, DISM, bcdboot).
#
# Run on Windows as Administrator.
#
# Flow:
#   1. Locate WinRE via reagentc /info
#   2. Temporarily disable WinRE (releases the WIM lock)
#   3. Mount winre.wim with DISM
#   4. Inject FileKeep recovery tools into the mounted image
#   5. Commit and unmount
#   6. Prepare the USB drive (FAT32, boot files via bcdboot)
#   7. Copy the customized winre.wim to the USB
#   8. Re-enable WinRE on the host
#
# Two modes:
#   1. Source mode (dev): publishes from a source tree.
#      .\winre\build-winre-usb.ps1 -DriveLetter E -SourceDir C:\ub\src
#   2. Binary mode (installed): uses pre-published binaries, e.g. from
#      C:\Program Files\FileKeep. The dashboard's WinRE GUI uses this mode.
#      .\winre\build-winre-usb.ps1 -DriveLetter E -BinaryDir "C:\Program Files\FileKeep"
#
# In binary mode the layout is expected to be:
#   <BinaryDir>\cli\FileKeep.exe
#   <BinaryDir>\wizard\FileKeepRecovery.exe
#   <BinaryDir>\docs\RECOVERY.md
#
# WARNING: Destructive — the target USB drive is formatted.

param(
    [Parameter(Mandatory=$true)]
    [string]$DriveLetter,
    [string]$SourceDir = "C:\ub\src",
    [string]$BinaryDir = "",
    [string]$MountDir = "C:\winre-mount",
    [switch]$SkipReenable
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Error $msg; exit 1 }

# --- Preconditions ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run as Administrator."
}
if (-not (Get-Command reagentc.exe -ErrorAction SilentlyContinue)) { Fail "reagentc.exe not found." }
if (-not (Get-Command dism.exe -ErrorAction SilentlyContinue)) { Fail "dism.exe not found." }
if (-not (Get-Command bcdboot.exe -ErrorAction SilentlyContinue)) { Fail "bcdboot.exe not found." }

# Normalize drive letter (accept "E", "E:", "E:\")
$DriveLetter = $DriveLetter.TrimEnd(':', '\')
if ($DriveLetter.Length -ne 1) { Fail "DriveLetter must be a single letter (e.g. E)." }
$usbRoot = "$DriveLetter`:\"
if (-not (Test-Path $usbRoot)) { Fail "Drive $usbRoot not found." }

# Refuse to run against the system drive or a fixed internal drive.
$vol = Get-Volume -DriveLetter $DriveLetter -ErrorAction SilentlyContinue
if ($vol -and $vol.DriveType -ne 'Removable') {
    Write-Warning "Drive $DriveLetter is not marked Removable (type: $($vol.DriveType))."
    $confirm = Read-Host "Type YES to continue anyway"
    if ($confirm -ne "YES") { Fail "Aborted." }
}

Write-Host "=== FileKeep WinRE USB builder ==="
Write-Host "Target USB: $usbRoot"

# --- Locate WinRE ---
Write-Host "Locating WinRE..."
$reInfo = reagentc /info 2>&1 | Out-String
$wimPath = $null
foreach ($line in $reInfo -split "`r?`n") {
    if ($line -match 'Windows RE location:\s*(.+)') {
        $loc = $Matches[1].Trim()
        # Location is like \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE
        # The WIM itself is winre.wim in that directory.
        if ($loc -match '^[A-Za-z]:\\') {
            $wimPath = Join-Path $loc "winre.wim"
        } else {
            Write-Host "WinRE location is not a plain path: $loc"
            Write-Host "Attempting to resolve via mountvol/GLOBALROOT..."
        }
        break
    }
}
# Fallback: probe the standard Recovery path on the system drive.
if (-not $wimPath -or -not (Test-Path $wimPath)) {
    $sysDrive = $env:SystemDrive
    foreach ($candidate in @(
        "$sysDrive\Recovery\WindowsRE\winre.wim",
        "$sysDrive\Windows\System32\Recovery\winre.wim"
    )) {
        if (Test-Path $candidate) { $wimPath = $candidate; break }
    }
}
if (-not $wimPath -or -not (Test-Path $wimPath)) {
    Fail "Could not locate winre.wim. Ensure WinRE is enabled (reagentc /info)."
}
Write-Host "WinRE image: $wimPath"

# --- Stage FileKeep binaries ---
$stageDir = Join-Path ([IO.Path]::GetTempPath()) "filekeep-winre-stage"
$cliOut = Join-Path $stageDir "cli"
$wizOut = Join-Path $stageDir "recovery"
$docsOut = Join-Path $stageDir "docs"
foreach ($d in @($cliOut, $wizOut, $docsOut)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

if ($BinaryDir -ne "") {
    Write-Host "Binary mode: copying pre-published binaries from $BinaryDir..."
    $srcCli = Join-Path $BinaryDir "cli\FileKeep.exe"
    $srcWiz = Join-Path $BinaryDir "wizard\FileKeepRecovery.exe"
    if (-not (Test-Path $srcCli)) { Fail "CLI not found: $srcCli" }
    if (-not (Test-Path $srcWiz)) { Fail "Wizard not found: $srcWiz" }
    Copy-Item (Join-Path $BinaryDir "cli\*") $cliOut -Recurse -Force
    Copy-Item (Join-Path $BinaryDir "wizard\*") $wizOut -Recurse -Force
    $srcDocs = Join-Path $BinaryDir "docs"
    if (Test-Path $srcDocs) { Copy-Item (Join-Path $srcDocs "*") $docsOut -Recurse -Force }
} else {
    Write-Host "Source mode: publishing from $SourceDir..."
    if (-not (Test-Path (Join-Path $SourceDir "FileKeep.slnx"))) { Fail "Source tree not found: $SourceDir" }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "dotnet SDK not found (source mode requires it)." }
    Push-Location $SourceDir
    try {
        dotnet publish src/UsenetBackup.Cli/UsenetBackup.Cli.csproj -c Release -r win-x64 --self-contained -o $cliOut
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish (CLI) failed." }
        dotnet publish src/UsenetBackup.Recovery/UsenetBackup.Recovery.csproj -c Release -r win-x64 --self-contained -o $wizOut
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish (Recovery) failed." }
    } finally { Pop-Location }
    $srcDocs = Join-Path $SourceDir "docs\RECOVERY.md"
    if (Test-Path $srcDocs) { Copy-Item $srcDocs $docsOut -Force }
}
Write-Host "Staged FileKeep binaries."

# --- Disable WinRE (releases the WIM lock) ---
$wasEnabled = $reInfo -match 'Enabled'
if ($wasEnabled) {
    Write-Host "Disabling WinRE temporarily..."
    reagentc /disable | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "reagentc /disable failed." }
} else {
    Write-Host "WinRE already disabled; proceeding."
}

$winreReenabled = $false
try {
    # --- Mount the WIM ---
    Write-Host "Mounting winre.wim..."
    if (Test-Path $MountDir) { Remove-Item $MountDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $MountDir | Out-Null
    # Copy the WIM to a temp location so the original stays pristine.
    $wimCopy = Join-Path ([IO.Path]::GetTempPath()) "filekeep-winre.wim"
    Copy-Item $wimPath $wimCopy -Force
    dism /Mount-Wim /WimFile:$wimCopy /Index:1 /MountDir:$MountDir
    if ($LASTEXITCODE -ne 0) { Fail "DISM mount failed." }

    try {
        # --- Inject FileKeep tools ---
        Write-Host "Injecting FileKeep recovery tools..."
        $fkDir = Join-Path $MountDir "FileKeep"
        New-Item -ItemType Directory -Force -Path $fkDir | Out-Null
        Copy-Item (Join-Path $stageDir "*") $fkDir -Recurse -Force

        # Add a startup hook so the recovery wizard launches in WinRE.
        # WinRE honors Winpeshl.ini for a custom shell.
        $winpeshl = Join-Path $MountDir "Windows\System32\winpeshl.ini"
        $winpeshlContent = @"
[LaunchApps]
X:\FileKeep\recovery\FileKeepRecovery.exe
"@
        # Only write if not already present to avoid clobbering.
        if (-not (Test-Path $winpeshl)) {
            $winpeshlContent | Out-File $winpeshl -Encoding ascii
            Write-Host "Added winpeshl.ini to launch the recovery wizard."
        } else {
            Write-Host "winpeshl.ini already exists; leaving it alone."
        }
    } finally {
        # --- Commit and unmount ---
        Write-Host "Committing and unmounting..."
        dism /Unmount-Wim /MountDir:$MountDir /Commit
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "DISM commit-unmount reported an error; attempting discard."
            dism /Unmount-Wim /MountDir:$MountDir /Discard | Out-Null
            Fail "DISM unmount /commit failed."
        }
    }

    # --- Prepare the USB drive ---
    Write-Host "Preparing USB drive $usbRoot (formatting as FAT32)..."
    # Use diskpart-free approach: format via Format-Volume, then bcdboot.
    Format-Volume -DriveLetter $DriveLetter -FileSystem FAT32 -NewFileSystemLabel "FILEKEEP" -Confirm:$false -Force
    if ($LASTEXITCODE -ne 0 -and $?) { Write-Host "Format complete." }

    Write-Host "Copying boot files..."
    bcdboot C:\Windows /s "$DriveLetter`:" /f UEFI
    if ($LASTEXITCODE -ne 0) { Fail "bcdboot failed." }

    Write-Host "Copying customized WinRE image to USB..."
    $usbSources = Join-Path $usbRoot "sources"
    New-Item -ItemType Directory -Force -Path $usbSources | Out-Null
    # Boot expects boot.wim; place our customized image there.
    Copy-Item $wimCopy (Join-Path $usbSources "boot.wim") -Force

    # Also copy the FileKeep tools to the USB root for easy access.
    $usbFk = Join-Path $usbRoot "FileKeep"
    New-Item -ItemType Directory -Force -Path $usbFk | Out-Null
    Copy-Item (Join-Path $stageDir "*") $usbFk -Recurse -Force

    Write-Host "USB build complete."
} finally {
    # --- Re-enable WinRE on the host ---
    if (-not $SkipReenable -and $wasEnabled) {
        Write-Host "Re-enabling WinRE..."
        reagentc /enable | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $winreReenabled = $true
            Write-Host "WinRE re-enabled."
        } else {
            Write-Warning "reagentc /enable failed. Run 'reagentc /enable' manually."
        }
    }
    # Cleanup temp files
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    $wimCopy = Join-Path ([IO.Path]::GetTempPath()) "filekeep-winre.wim"
    Remove-Item $wimCopy -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== Done ==="
Write-Host "Bootable FileKeep recovery USB created on $usbRoot"
if (-not $winreReenabled -and $wasEnabled) {
    Write-Warning "WinRE was NOT re-enabled automatically. Run 'reagentc /enable' as Administrator."
}
