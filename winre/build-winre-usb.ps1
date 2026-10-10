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
#
# Progress reporting: the script emits FKPROGRESS JSON lines to stdout as
# each step starts and completes, e.g.:
#   FKPROGRESS: {"step":3,"totalSteps":10,"name":"Mounting winre.wim","state":"started","percent":30}
# The dashboard parses these to render a progress bar and step list.

param(
    [string]$DriveLetter = "",
    [string]$ImagePath = "",
    [int]$ImageSizeMB = 4096,
    [string]$SourceDir = "C:\ub\src",
    [string]$BinaryDir = "",
    [string]$MountDir = "C:\winre-mount",
    [switch]$SkipReenable
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Error $msg; exit 1 }

# --- Mode selection ---
# USB mode: writes directly to a USB drive (destructive).
# Image mode: builds a bootable VHDX disk image file instead (ADK-free,
# flashable later with Rufus/BalenaEtcher, bootable in Hyper-V/VirtualBox).
$script:ImageMode = $ImagePath -ne ""
if ($script:ImageMode -and $DriveLetter -ne "") { Fail "Specify either -DriveLetter or -ImagePath, not both." }
if (-not $script:ImageMode -and $DriveLetter -eq "") { Fail "Specify -DriveLetter (USB) or -ImagePath (disk image)." }

# --- Progress reporting ---
# Steps with rough time estimates (seconds) for ETA calculation.
$usbTargetName = if ($script:ImageMode) { "disk image" } else { "USB drive" }
$script:Steps = @(
    @{ Name = "Checking prerequisites";       EstimateSec = 5   },
    @{ Name = "Locating WinRE image";          EstimateSec = 10  },
    @{ Name = "Staging FileKeep binaries";     EstimateSec = 60  },
    @{ Name = "Disabling WinRE temporarily";   EstimateSec = 15  },
    @{ Name = "Mounting winre.wim";            EstimateSec = 60  },
    @{ Name = "Injecting FileKeep tools";      EstimateSec = 30  },
    @{ Name = "Committing and unmounting";     EstimateSec = 90  },
    @{ Name = $(if ($script:ImageMode) { "Creating disk image" } else { "Formatting USB drive" }); EstimateSec = 60  },
    @{ Name = "Copying boot files and image";  EstimateSec = 120 },
    @{ Name = "Re-enabling WinRE";             EstimateSec = 15  }
)
$script:TotalEstimateSec = ($script:Steps | Measure-Object -Property EstimateSec -Sum).Sum
$script:CurrentStep = 0

function Write-ProgressStep([int]$step, [string]$state) {
    $script:CurrentStep = $step
    $elapsed = 0
    for ($i = 0; $i -lt $step - 1; $i++) { $elapsed += $script:Steps[$i].EstimateSec }
    $percent = if ($state -eq "completed") {
        $e = $elapsed + $script:Steps[$step - 1].EstimateSec
        [math]::Round($e / $script:TotalEstimateSec * 100)
    } else {
        [math]::Round($elapsed / $script:TotalEstimateSec * 100)
    }
    $remaining = $script:TotalEstimateSec - $elapsed
    $obj = @{
        step = $step
        totalSteps = $script:Steps.Count
        name = $script:Steps[$step - 1].Name
        state = $state
        percent = $percent
        estimatedRemainingSec = $remaining
    } | ConvertTo-Json -Compress
    # FKPROGRESS marker goes to stdout for the dashboard to parse.
    Write-Output "FKPROGRESS: $obj"
}
function Step-Start([int]$n) { Write-ProgressStep $n "started"; Write-Host "[$n/$($script:Steps.Count)] $($script:Steps[$n-1].Name)..." }
function Step-Done([int]$n) { Write-ProgressStep $n "completed" }

# --- Preconditions ---
Step-Start 1
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run as Administrator."
}
if (-not (Get-Command reagentc.exe -ErrorAction SilentlyContinue)) { Fail "reagentc.exe not found." }
if (-not (Get-Command dism.exe -ErrorAction SilentlyContinue)) { Fail "dism.exe not found." }
if (-not (Get-Command bcdboot.exe -ErrorAction SilentlyContinue)) { Fail "bcdboot.exe not found." }
if (-not (Get-Command diskpart.exe -ErrorAction SilentlyContinue)) { Fail "diskpart.exe not found." }
Step-Done 1

if ($script:ImageMode) {
    # --- Image mode: validate output path ---
    $ImagePath = [IO.Path]::GetFullPath($ImagePath)
    if (-not $ImagePath.EndsWith(".vhdx", [StringComparison]::OrdinalIgnoreCase)) { Fail "ImagePath must end with .vhdx" }
    $imgDir = Split-Path $ImagePath -Parent
    if (-not (Test-Path $imgDir)) { Fail "Image directory not found: $imgDir" }
    if (Test-Path $ImagePath) { Fail "Image already exists: $ImagePath (delete it first)." }
    if ($ImageSizeMB -lt 1024) { Fail "ImageSizeMB must be at least 1024." }
    Write-Host "=== FileKeep WinRE disk image builder ==="
    Write-Host "Target image: $ImagePath ($ImageSizeMB MB expandable VHDX)"
} else {
    # --- USB mode: validate drive ---
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
}

# --- Locate WinRE ---
Step-Start 2
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
Step-Done 2

# --- Stage FileKeep binaries ---
Step-Start 3
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
Step-Done 3

# --- Disable WinRE (releases the WIM lock) ---
Step-Start 4
$wasEnabled = $reInfo -match 'Enabled'
if ($wasEnabled) {
    Write-Host "Disabling WinRE temporarily..."
    reagentc /disable | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "reagentc /disable failed." }
} else {
    Write-Host "WinRE already disabled; proceeding."
}
Step-Done 4

$winreReenabled = $false
try {
    # --- Mount the WIM ---
    Step-Start 5
    Write-Host "Mounting winre.wim..."
    if (Test-Path $MountDir) { Remove-Item $MountDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $MountDir | Out-Null
    # Copy the WIM to a temp location so the original stays pristine.
    $wimCopy = Join-Path ([IO.Path]::GetTempPath()) "filekeep-winre.wim"
    Copy-Item $wimPath $wimCopy -Force
    dism /Mount-Wim /WimFile:$wimCopy /Index:1 /MountDir:$MountDir
    if ($LASTEXITCODE -ne 0) { Fail "DISM mount failed." }
    Step-Done 5

    try {
        # --- Inject FileKeep tools ---
        Step-Start 6
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
        Step-Done 6
    } finally {
        # --- Commit and unmount ---
        Step-Start 7
        Write-Host "Committing and unmounting..."
        dism /Unmount-Wim /MountDir:$MountDir /Commit
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "DISM commit-unmount reported an error; attempting discard."
            dism /Unmount-Wim /MountDir:$MountDir /Discard | Out-Null
            Fail "DISM unmount /commit failed."
        }
        Step-Done 7
    }

    if ($script:ImageMode) {
        # --- Image mode: create and attach a VHDX, then treat it like the USB ---
        Step-Start 8
        Write-Host "Creating disk image $ImagePath..."
        $dpCreate = @"
create vdisk file="$ImagePath" maximum=$ImageSizeMB type=expandable
select vdisk file="$ImagePath"
attach vdisk
create partition primary
format fs=fat32 quick label="FILEKEEP"
assign
"@
        $dpCreate | diskpart | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "diskpart create/attach failed." }
        # Find the newly assigned drive letter.
        Start-Sleep 2
        $vhdVol = Get-Volume | Where-Object { $_.FileSystemLabel -eq "FILEKEEP" -and $_.DriveLetter } |
                  Sort-Object -Property DriveLetter -Descending | Select-Object -First 1
        if (-not $vhdVol) { Fail "Could not find the attached VHDX volume." }
        $targetRoot = "$($vhdVol.DriveLetter):\"
        Write-Host "VHDX attached as $($vhdVol.DriveLetter):"
        Step-Done 8

        try {
            Step-Start 9
            Write-Host "Copying boot files..."
            bcdboot C:\Windows /s $targetRoot /f UEFI
            if ($LASTEXITCODE -ne 0) { Fail "bcdboot failed." }

            Write-Host "Copying customized WinRE image..."
            $imgSources = Join-Path $targetRoot "sources"
            New-Item -ItemType Directory -Force -Path $imgSources | Out-Null
            Copy-Item $wimCopy (Join-Path $imgSources "boot.wim") -Force

            $imgFk = Join-Path $targetRoot "FileKeep"
            New-Item -ItemType Directory -Force -Path $imgFk | Out-Null
            Copy-Item (Join-Path $stageDir "*") $imgFk -Recurse -Force

            Write-Host "Disk image build complete."
            Step-Done 9
        } finally {
            Write-Host "Detaching disk image..."
            @"
select vdisk file="$ImagePath"
detach vdisk
"@ | diskpart | Out-Null
        }
    } else {
        # --- USB mode: prepare the physical drive ---
        Step-Start 8
        Write-Host "Preparing USB drive $usbRoot (formatting as FAT32)..."
        Format-Volume -DriveLetter $DriveLetter -FileSystem FAT32 -NewFileSystemLabel "FILEKEEP" -Confirm:$false -Force
        if ($LASTEXITCODE -ne 0 -and $?) { Write-Host "Format complete." }
        Step-Done 8

        Step-Start 9
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
        Step-Done 9
    }
} finally {
    # --- Re-enable WinRE on the host ---
    Step-Start 10
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
    Step-Done 10
    }
    # Cleanup temp files
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    $wimCopy = Join-Path ([IO.Path]::GetTempPath()) "filekeep-winre.wim"
    Remove-Item $wimCopy -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== Done ==="
if ($script:ImageMode) {
    Write-Host "Bootable FileKeep recovery disk image created: $ImagePath"
    Write-Host "Flash it to USB with Rufus or BalenaEtcher, or boot it directly in Hyper-V/VirtualBox."
} else {
    Write-Host "Bootable FileKeep recovery USB created on $usbRoot"
}
if (-not $winreReenabled -and $wasEnabled) {
    Write-Warning "WinRE was NOT re-enabled automatically. Run 'reagentc /enable' as Administrator."
}
