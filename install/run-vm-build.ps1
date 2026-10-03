# One-shot VM build for Usenet Backup.
# Downloads the fixed installer source + WinPE script, re-publishes the apps
# as single-file executables, builds the MSI, test-installs it, verifies,
# then uninstalls to leave the machine clean.
#
# On the VM, run:
#   iwr "<this-script-url>" -OutFile C:\ub\run.ps1
#   C:\ub\run.ps1

$ErrorActionPreference = "Stop"

$wxsUrl   = "https://tmpfiles.org/dl/1791046072.49d2a774618e5bcd/wZABeBxWhMbe/usenetbackup.wxs"
$winpeUrl = "https://tmpfiles.org/dl/1791046084.f842814c75d93198/wEAcebxKha8e/build-winpe.ps1"

Write-Host "=== 1/5 Downloading fixed installer source + WinPE script ==="
iwr $wxsUrl -OutFile C:\ub\src\install\UsenetBackup.wxs
iwr $winpeUrl -OutFile C:\ub\src\winpe\build-winpe.ps1
Write-Host ("  UsenetBackup.wxs: {0:N0} bytes" -f (Get-Item C:\ub\src\install\UsenetBackup.wxs).Length)
Write-Host ("  build-winpe.ps1:  {0:N0} bytes" -f (Get-Item C:\ub\src\winpe\build-winpe.ps1).Length)

Write-Host "=== 2/5 Re-publishing apps as single-file ==="
cd C:\ub\src
dotnet publish src/UsenetBackup.Cli/UsenetBackup.Cli.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\cli /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }
dotnet publish src/UsenetBackup.Service/UsenetBackup.Service.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\service /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }
dotnet publish src/UsenetBackup.Recovery/UsenetBackup.Recovery.csproj -c Release -r win-x64 --self-contained -o C:\ub\publish\recovery /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Recovery publish failed" }

Write-Host "=== 3/5 Verifying single-file publishes ==="
foreach ($d in @("C:\ub\publish\cli", "C:\ub\publish\service", "C:\ub\publish\recovery")) {
    $n = (Get-ChildItem $d).Count
    Write-Host "  $d : $n files"
    if ($n -gt 15) { throw "Publish at $d does not look single-file ($n files)" }
}
foreach ($f in @("C:\ub\publish\cli\usenet-backup.exe",
                 "C:\ub\publish\service\usenet-backup-service.exe",
                 "C:\ub\publish\recovery\usenet-backup-recovery.exe")) {
    if (-not (Test-Path $f)) { throw "Missing publish output: $f" }
}
Write-Host "  All three exes present."

Write-Host "=== 4/5 Building MSI ==="
wix build -arch x64 -d CliBin=C:\ub\publish\cli -d ServiceBin=C:\ub\publish\service -d RecoveryBin=C:\ub\publish\recovery -o C:\ub\usenet-backup-0.8.0-x64.msi install/UsenetBackup.wxs
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
$msi = Get-Item C:\ub\usenet-backup-0.8.0-x64.msi
Write-Host ("  MSI built: {0:N0} bytes" -f $msi.Length)

Write-Host "=== 5/5 Test install + verify + uninstall ==="
Start-Process msiexec -ArgumentList "/i", "C:\ub\usenet-backup-0.8.0-x64.msi", "/qn" -Wait
Start-Sleep 15
foreach ($c in @("C:\Program Files\UsenetBackup\cli\usenet-backup.exe",
                 "C:\Program Files\UsenetBackup\service\usenet-backup-service.exe",
                 "C:\Program Files\UsenetBackup\wizard\usenet-backup-recovery.exe")) {
    if (Test-Path $c) { Write-Host "  OK installed: $c" } else { throw "MISSING after install: $c" }
}
$svc = sc.exe query UsenetBackup
Write-Host "  Service state:"
Write-Host $svc
if ($svc -notmatch "RUNNING") { Write-Host "  WARNING: service not running after install" }

Write-Host "  Uninstalling..."
Start-Process msiexec -ArgumentList "/x", "C:\ub\usenet-backup-0.8.0-x64.msi", "/qn" -Wait
Start-Sleep 10
if (Test-Path "C:\Program Files\UsenetBackup") { throw "Uninstall left files behind" }
Write-Host "  Uninstall clean."

Write-Host ""
Write-Host "ALL DONE — MSI built, installed, verified, and uninstalled cleanly."
