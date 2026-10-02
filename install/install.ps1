#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or uninstalls) the Usenet Backup Windows service.

.DESCRIPTION
    Copies the published service binary to the install directory, registers
    it with the Service Control Manager via sc.exe, and stores the repository
    passphrase as a machine-level environment variable for the service
    account. The dashboard listens on loopback only (127.0.0.1:15789 by
    default); do not expose it without a reverse proxy.

    Publish the service first (needs network for NuGet on first run):
        dotnet publish src/UsenetBackup.Service/UsenetBackup.Service.csproj `
            -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -o publish/service

    Then:
        .\install.ps1 -Source .\publish\service -Config .\service.json

.PARAMETER Source
    Directory containing the published usenet-backup-service.exe.

.PARAMETER Config
    Your service.json (see src/UsenetBackup.Service/service.example.json).
    Copied next to the binary as service.json.

.PARAMETER InstallDir
    Install location. Defaults to C:\Program Files\UsenetBackup.

.PARAMETER ServiceName
    SCM service name. Defaults to UsenetBackup.

.PARAMETER Passphrase
    Repository passphrase as a SecureString. If omitted, you are prompted
    interactively. Useful for automated installs.

.PARAMETER Uninstall
    Stop and remove the service instead of installing.
#>
param(
    [string]$Source = "",
    [string]$Config = "",
    [string]$InstallDir = "C:\Program Files\UsenetBackup",
    [string]$ServiceName = "UsenetBackup",
    [SecureString]$Passphrase,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

function Fail([string]$msg) { Write-Error $msg; exit 1 }

if ($Uninstall) {
    Write-Host "Stopping service $ServiceName..."
    sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Deleting service $ServiceName..."
    $out = sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) { Fail "sc.exe delete failed: $out" }
    Write-Host "Uninstalled. The machine-level USENETBACKUP_PASSPHRASE was left in place;"
    Write-Host "remove it manually if it is no longer needed:"
    Write-Host '  [Environment]::SetEnvironmentVariable("USENETBACKUP_PASSPHRASE", $null, "Machine")'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path $Source)) { Fail "Source directory '$Source' not found. Publish the service first (see script header)." }
if ([string]::IsNullOrWhiteSpace($Config) -or -not (Test-Path $Config)) { Fail "Config file '$Config' not found. Copy src/UsenetBackup.Service/service.example.json and edit it." }

$exe = Join-Path $Source "usenet-backup-service.exe"
if (-not (Test-Path $exe)) { Fail "Expected $exe to exist. Did the publish succeed?" }

Write-Host "Installing to $InstallDir..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $Source "*") $InstallDir -Recurse -Force
Copy-Item $Config (Join-Path $InstallDir "service.json") -Force

# The service needs the passphrase non-interactively. Store it as a
# machine-level environment variable (readable by admins + SYSTEM only
# via ACLs on the registry key in practice — treat this machine as trusted).
if (-not $Passphrase) {
    $Passphrase = Read-Host "Repository passphrase (stored as machine env USENETBACKUP_PASSPHRASE)" -AsSecureString
}
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Passphrase))
[Environment]::SetEnvironmentVariable("USENETBACKUP_PASSPHRASE", $plain, "Machine")
$plain = $null

$binPath = "`"$(Join-Path $InstallDir 'usenet-backup-service.exe')`""
$existing = sc.exe query $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service already exists; updating binary path..."
    sc.exe config $ServiceName binPath= $binPath | Out-Null
} else {
    Write-Host "Creating service $ServiceName..."
    $out = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "Usenet Backup Service"
    if ($LASTEXITCODE -ne 0) { Fail "sc.exe create failed: $out" }
    sc.exe description $ServiceName "Scheduled encrypted backups to Usenet (usenet-backup)." | Out-Null
}

Write-Host "Starting service..."
sc.exe start $ServiceName | Out-Null
Write-Host ""
Write-Host "Installed and started. Dashboard: http://127.0.0.1:15789/ (loopback only)"
Write-Host "Service log: $(Join-Path $InstallDir 'service.log')"
Write-Host "Job activity is also appended to each repo's operations.log."
