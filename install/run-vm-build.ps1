# VM build for FileKeep.
# Builds from a synced source tree: the installer and recovery sources
# come from that tree, not from embedded copies in this script.
# (Embedded copies were dropped: they went stale silently whenever the
# repo files changed, so the build shipped old installer/recovery code.)
#
# On the build machine, sync the full source tree first (e.g. to
# C:\ub\src), then run the script from inside that tree:
#   C:\ub\src\install\run-vm-build.ps1
# Do NOT copy this script alone to a fresh machine and run it there.

$ErrorActionPreference = "Stop"

# Code signing configuration (all optional - build works without signing).
# Set these as environment variables on the build VM:
#   UB_SIGN_THUMBPRINT - Certificate thumbprint in LocalMachine\My store
#   UB_SIGN_PFX        - Path to .pfx/.p12 certificate file
#   UB_SIGN_PFX_PASSWORD - Password for the PFX file (consider using a secure vault)
#   UB_SKIP_SIGNING    - Set to "1" to explicitly skip signing
#
# The signing step uses signtool.exe from the Windows SDK. Timestamping is
# always applied so signatures remain valid after the cert expires.

function Invoke-CodeSigning {
    param([string[]]$Files)

    if ($env:UB_SKIP_SIGNING -eq "1") {
        Write-Host "  Signing skipped (UB_SKIP_SIGNING=1)."
        return
    }

    $signtool = $null
    # Version-agnostic: pick the newest installed Windows SDK's signtool.
    $signtool = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1).FullName
    if (-not $signtool) {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    }
    if (-not $signtool -and (Test-Path "C:\ub\signtool.exe")) {
        # Standalone signtool from the Microsoft.Windows.SDK.BuildTools NuGet package.
        $signtool = "C:\ub\signtool.exe"
    }
    if (-not $signtool) {
        Write-Host "  WARNING: signtool.exe not found - skipping code signing."
        Write-Host "  Install the Windows SDK to enable signing."
        return
    }

    $timestampUrl = "http://timestamp.digicert.com"
    $signed = 0

    foreach ($f in $Files) {
        if (-not (Test-Path $f)) {
            Write-Host "  WARNING: file not found, skipping: $f"
            continue
        }

        $args = @("sign", "/tr", $timestampUrl, "/td", "sha256", "/fd", "sha256")

        if ($env:UB_SIGN_THUMBPRINT) {
            # /sm: the cert lives in LocalMachine\My; without it signtool
            # only searches the current user store and finds nothing.
            $args += @("/sm", "/sha1", $env:UB_SIGN_THUMBPRINT)
            Write-Host "  Signing $f with cert thumbprint $($env:UB_SIGN_THUMBPRINT.Substring(0,8))..."
        } elseif ($env:UB_SIGN_PFX) {
            $args += @("/f", $env:UB_SIGN_PFX)
            if ($env:UB_SIGN_PFX_PASSWORD) {
                $args += @("/p", $env:UB_SIGN_PFX_PASSWORD)
            }
            Write-Host "  Signing $f with PFX $env:UB_SIGN_PFX..."
        } else {
            Write-Host "  No signing certificate configured (set UB_SIGN_THUMBPRINT or UB_SIGN_PFX)."
            Write-Host "  Skipping code signing - build will be unsigned."
            return
        }

        $args += $f
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $f" }

        # Verify the signature
        & $signtool verify /pa $f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $f" }

        Write-Host "  Signed and verified: $f"
        $signed++
    }

    Write-Host "  Signed $signed file(s)."
}

# Log everything to C:\ub\build.log: RDP drops do not kill the script on the
# VM, so if the session disconnects, reconnect and read the log for progress.
$transcript = "C:\ub\build.log"
try { Stop-Transcript | Out-Null } catch { }
Start-Transcript -Path $transcript -Append | Out-Null
Write-Host "Logging to $transcript"

try {

Write-Host "=== 1/6 Using installer and recovery sources from the synced tree ==="
Write-Host "  No embedded copies: install/FileKeep.wxs and winre/build-winre-usb.ps1 come from the source tree (checked in step 2)."


Write-Host "=== 2/6 Re-publishing apps as single-file ==="
# Single source root used consistently: the script cds here AND passes it to
# WiX as SrcRoot. (Previously these were two different hardcoded paths,
# Source root: use src-new if it exists (current VM layout), else src.
$srcRoot = if (Test-Path "C:\ub\src-new") { "C:\ub\src-new" } else { "C:\ub\src" }
cd $srcRoot

# The build reads these from the source tree (no embedded copies remain):
# fail here, before publishing, if the sync did not bring them.
foreach ($srcFile in @("install\FileKeep.wxs", "install\Bundle.wxs", "winre\build-winre-usb.ps1")) {
    if (-not (Test-Path (Join-Path $srcRoot $srcFile))) { throw "Missing source file for build: $srcFile (sync the full source tree to $srcRoot first; this script no longer carries embedded copies)." }
}

# Product version: the repo-root VERSION file is the single source of truth.
# It stamps the assemblies (the /p: props below), the MSI package and the
# bundle (via -d ProductVersion=..., consumed as $(var.ProductVersion) in
# the .wxs files), and the MSI file name. Hardcode it nowhere else.
$versionPath = Join-Path $srcRoot "VERSION"
if (-not (Test-Path $versionPath)) { throw "VERSION file not found: $versionPath" }
$productVersion = (Get-Content $versionPath -Raw).Trim()
if ($productVersion -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION file must contain an n.nn.nn version, found: '$productVersion'" }
Write-Host "Product version (from VERSION): $productVersion"
$msiPath = "C:\ub\filekeep-$productVersion.msi"

# Drift guard: a hardcoded Version="n.nn.nn" literal in either .wxs would silently
# override VERSION and resurrect the phantom-update bug - fail loudly.
$versionLiteral = [regex]'\sVersion="(\d+\.\d+\.\d+)"'
foreach ($wxsFile in @("$srcRoot\install\FileKeep.wxs", "$srcRoot\install\Bundle.wxs")) {
    $hit = $versionLiteral.Match((Get-Content $wxsFile -Raw))
    if ($hit.Success) { throw "Version drift in $wxsFile - hardcoded Version=`"$($hit.Groups[1].Value)`". Use Version=`"`$(var.ProductVersion)`" there; the only version stamp is the repo-root VERSION file ($productVersion)." }
}
# Stamp file versions so Windows Installer replaces binaries on upgrade/reinstall.
# (Without this, every build carries the default FileVersion 1.0.0.0 and a
# reinstall silently keeps the previously installed binaries.)
# Build = days since 2026-01-01, revision = minutes since midnight: two builds
# on the same day still get distinct versions, so a same-day rebuild +
# reinstall actually replaces the binaries.
$now = Get-Date
$buildNo = [int](($now - (Get-Date "2026-01-01")).TotalDays)
$revNo = $now.Hour * 60 + $now.Minute
$fileVer = "0.8.$buildNo.$revNo"
$verProps = @("/p:Version=$productVersion", "/p:FileVersion=$fileVer",
              "/p:AssemblyVersion=$productVersion", "/p:InformationalVersion=$productVersion")
Write-Host "Stamping FileVersion $fileVer"
dotnet publish src/FileKeep.Cli/FileKeep.Cli.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\cli /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }
dotnet publish src/FileKeep.Service/FileKeep.Service.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\service /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }
dotnet publish src/FileKeep.Recovery/FileKeep.Recovery.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\recovery /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "Recovery publish failed" }
dotnet publish src/FileKeep.Gui/FileKeep.Gui.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\gui /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }
# Portable reader (Stealth Folders slice 3, part 3): published and
# signed like the other exes, but NOT packaged into the MSI — it
# is a portable stick tool, pointed at by the WinRE reader step
# and copied by hand, not an installed program.
dotnet publish src/FileKeep.Reader/FileKeep.Reader.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\reader /p:PublishSingleFile=true @verProps
if ($LASTEXITCODE -ne 0) { throw "Reader publish failed" }

Write-Host "=== 3/6 Verifying required MSI payload files ==="
$required = @(
    "C:\ub\publish\cli\FileKeep.exe",
    "C:\ub\publish\service\FileKeepService.exe",
    "C:\ub\publish\recovery\FileKeepRecovery.exe",
    "C:\ub\publish\gui\FileKeepGui.exe"
)
foreach ($f in $required) {
    if (Test-Path $f) { Write-Host ("  OK: {0} ({1:N0} bytes)" -f $f, (Get-Item $f).Length) }
    else { throw "MISSING required MSI payload: $f" }
}

Write-Host "=== 4/6 Building MSI ==="
# Sign the executables BEFORE building the MSI so the signed binaries are packaged.
Write-Host "  Signing executables..."
Invoke-CodeSigning -Files @(
    "C:\ub\publish\cli\FileKeep.exe",
    "C:\ub\publish\service\FileKeepService.exe",
    "C:\ub\publish\recovery\FileKeepRecovery.exe",
    "C:\ub\publish\gui\FileKeepGui.exe",
    "C:\ub\publish\reader\FileKeepReader.exe"
)

# Build the VSS helper (MSVC or MinGW)
Write-Host "Building VSS helper..."
& "$srcRoot\src\FileKeep.VssHelper\build-vss-helper.ps1"
if ($LASTEXITCODE -ne 0) { throw "VSS helper build failed" }

wix extension add WixToolset.Util.wixext/6.0.1
if ($LASTEXITCODE -ne 0) { throw "wix Util extension add failed" }
wix build -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -d SrcRoot=$srcRoot -d ProductVersion=$productVersion -d CliBin=C:\ub\publish\cli -d ServiceBin=C:\ub\publish\service -d RecoveryBin=C:\ub\publish\recovery -d GuiBin=C:\ub\publish\gui -d VssBin=$srcRoot\src\FileKeep.VssHelper -o $msiPath install/FileKeep.wxs
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
$msi = Get-Item $msiPath
Write-Host ("  MSI built: {0:N0} bytes" -f $msi.Length)

# Branded Burn bootstrapper: stock WixStandardBootstrapperApplication with the
# FileKeep logo (see install/Bundle.wxs). No custom theme, so no splash
# copy is needed - the old FileKeepTheme files were unreferenced and are removed.
# NOTE: the Bal NuGet package (WixToolset.Bal.wixext) ships its assembly as
# WixToolset.BootstrapperApplications.wixext.dll, so the -ext moniker cannot
# resolve it by name. Pass the assembly path explicitly.
$balDll = Get-ChildItem "$env:USERPROFILE\.wix\extensions\WixToolset.Bal.wixext" -Recurse `
    -Filter "WixToolset.BootstrapperApplications.wixext.dll" -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $balDll) {
    Write-Host "  Bal extension not installed; acquiring..."
    wix extension add --global WixToolset.Bal.wixext
    if ($LASTEXITCODE -ne 0) { throw "wix extension add failed" }
    $balDll = Get-ChildItem "$env:USERPROFILE\.wix\extensions\WixToolset.Bal.wixext" -Recurse `
        -Filter "WixToolset.BootstrapperApplications.wixext.dll" |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $balDll) { throw "Bal extension assembly not found" }
# A previous installer left on its Success page still holds C:\ub\FileKeep-Setup.exe
# open, which makes the bundle step fail cryptically with WIX7012 (access denied).
# Fail early with the actual cause instead of letting WiX report it.
$staleSetup = Get-Process FileKeep-Setup -ErrorAction SilentlyContinue
if ($staleSetup) {
    throw "FileKeep-Setup.exe is still running (PIDs: $(($staleSetup.Id) -join ', ')) - close the installer (its window X, not just the app) and rebuild. Leaving it on the Success page blocks the bundle step."
}
wix build -arch x64 -ext $balDll -d SrcRoot=$srcRoot -d ProductVersion=$productVersion -d MsiPath=$msiPath -o C:\ub\FileKeep-Setup.exe "$srcRoot\install\Bundle.wxs"
if ($LASTEXITCODE -ne 0) { throw "wix bundle build failed" }
$bundle = Get-Item C:\ub\FileKeep-Setup.exe
Write-Host ("  Bundle built: {0:N0} bytes" -f $bundle.Length)

Write-Host "=== 5/6 Signing MSI + bundle ==="
# Sign the MSI directly.
Invoke-CodeSigning -Files @($msiPath)

# A Burn bundle must be signed in two pieces: detach the engine, sign it,
# reattach it, then sign the whole bundle. Direct-signing the finished
# bundle corrupts the recorded attached-container byte offsets and causes
# a spurious source-resolution file dialog at install time
# (Failed to acquire container: WixAttachedContainer).
Write-Host "  Detaching Burn engine..."
$engine = "C:\ub\burnengine.exe"
$reattached = "C:\ub\FileKeep-Setup-signed.exe"
& wix burn detach C:\ub\FileKeep-Setup.exe -engine $engine
if ($LASTEXITCODE -ne 0) { throw "wix burn detach failed" }
Invoke-CodeSigning -Files @($engine)
Write-Host "  Reattaching signed engine..."
& wix burn reattach C:\ub\FileKeep-Setup.exe -engine $engine -o $reattached
if ($LASTEXITCODE -ne 0) { throw "wix burn reattach failed" }
Invoke-CodeSigning -Files @($reattached)
Move-Item -Force $reattached C:\ub\FileKeep-Setup.exe
Write-Host "  Bundle signed via detach/sign/reattach/sign."

Write-Host "=== 6/6 Test install + verify + uninstall ==="
# A running GUI blocks file replacement on install (in-use exe) and directory
# removal on uninstall (gui/ as its working directory) - stop it first so the
# self-test tests the new build instead of the leftovers of the old one.
Get-Process FileKeepGui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 2
Start-Process msiexec -ArgumentList "/i", $msiPath, "/qn", "/l*v", "C:\ub\selftest-install.log" -Wait
Start-Sleep 15
foreach ($c in @("C:\Program Files\FileKeep\cli\FileKeep.exe",
                 "C:\Program Files\FileKeep\service\FileKeepService.exe",
                 "C:\Program Files\FileKeep\wizard\FileKeepRecovery.exe",
                 "C:\Program Files\FileKeep\gui\FileKeepGui.exe")) {
    if (Test-Path $c) { Write-Host "  OK installed: $c" } else { throw "MISSING after install: $c" }
}
$svc = sc.exe query FileKeep
Write-Host "  Service state:"
Write-Host $svc
# -notmatch on an array returns the non-matching lines (always truthy) - join first.
if (($svc -join "`n") -notmatch "RUNNING") { Write-Host "  NOTE: service not running after install (expected: registered auto-start, starts on first boot / service start)" }

Write-Host "  Uninstalling..."
# Same reason as before install: a running GUI blocks directory removal.
Get-Process FileKeepGui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 2
# Uninstall by ProductCode from the registered product (not by MSI path),
# because each build generates a new ProductCode.
$prods = @(Get-WmiObject Win32_Product | Where-Object { $_.Name -eq "FileKeep" })
if ($prods.Count -gt 0) {
    foreach ($prod in $prods) {
        Write-Host ("  Uninstalling product: {0}" -f $prod.IdentifyingNumber)
        Start-Process msiexec -ArgumentList "/x", $prod.IdentifyingNumber, "/qn", "/l*v", "C:\ub\selftest-uninstall.log" -Wait
        Start-Sleep 5
    }
    Start-Sleep 5
} else {
    Write-Host "  WARNING: FileKeep not found in Win32_Product, trying MSI path..."
    Start-Process msiexec -ArgumentList "/x", $msiPath, "/qn", "/l*v", "C:\ub\selftest-uninstall.log" -Wait
    Start-Sleep 10
}
if (Test-Path "C:\Program Files\FileKeep") { throw "Uninstall left files behind" }
if (Test-Path "$env:LOCALAPPDATA\FileKeep") { throw "Uninstall left LocalAppData files behind" }
Write-Host "  Uninstall clean."

Write-Host ""
Write-Host "ALL DONE - MSI built, installed, verified, and uninstalled cleanly."
} finally {
    Stop-Transcript | Out-Null
}
