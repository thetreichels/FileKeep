#Requires -RunAsAdministrator
<#
.SYNOPSIS
    End-to-end validation of the Windows-only paths on a Windows 10/11 machine.

.DESCRIPTION
    Exercises everything that cannot be tested on Linux:
      1. VSS snapshot backup (backup --vss) — including reading an
         exclusively-locked file that a plain backup cannot read, and
         verifying the manifest records snapshot="vss".
      2. VSS shadow-copy hygiene — no shadow copies leak after the backup.
      3. Disk image backup/restore round-trip (file-backed, safe).
      4. Windows service install/start, dashboard reachability, a
         dashboard-triggered backup run, then clean uninstall.

    The script publishes the CLI and service itself when -RepoRoot is given
    (needs the .NET SDK), or uses prebuilt -CliDir / -ServiceDir outputs.
    All test artifacts live under -WorkRoot (default: ProgramData) and are
    removed afterwards. The validation service is named UsenetBackupValidate
    so it never touches a real UsenetBackup installation.

.EXAMPLE
    .\validate-windows.ps1 -RepoRoot C:\src\usenet-backup
.EXAMPLE
    .\validate-windows.ps1 -CliDir C:\pub\cli -ServiceDir C:\pub\service
#>
param(
    [string]$RepoRoot = "",
    [string]$CliDir = "",
    [string]$ServiceDir = "",
    [string]$WorkRoot = (Join-Path $env:ProgramData "usenet-backup-validate")
)

$ErrorActionPreference = "Stop"
$results = [System.Collections.Generic.List[object]]::new()

function Check([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    try {
        & $body
        $results.Add([pscustomobject]@{ Check = $name; Result = "PASS"; Detail = "" })
        Write-Host "PASS" -ForegroundColor Green
    } catch {
        $results.Add([pscustomobject]@{ Check = $name; Result = "FAIL"; Detail = $_.Exception.Message })
        Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-True([bool]$cond, [string]$msg) { if (-not $cond) { throw $msg } }

function Get-BackupId([string]$cliOutput) {
    $m = [regex]::Match($cliOutput, "backup ([0-9a-f]{32})")
    if (-not $m.Success) { throw "could not parse backup id from CLI output: $cliOutput" }
    return $m.Groups[1].Value
}

function Report {
    Write-Host "`n===== VALIDATION RESULTS =====" -ForegroundColor Yellow
    $results | Format-Table -AutoSize | Out-String | Write-Host
    $failed = @($results | Where-Object { $_.Result -eq "FAIL" })
    if ($failed.Count -gt 0) {
        Write-Host "$($failed.Count) check(s) FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
}

# --- random passphrase for the throwaway repos (never leaves this machine) ---
$pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
$env:USENETBACKUP_PASSPHRASE = $pw
$secPw = ConvertTo-SecureString $pw -AsPlainText -Force

# Remember any pre-existing machine-level passphrase so we can restore it.
$prevMachinePw = [Environment]::GetEnvironmentVariable("USENETBACKUP_PASSPHRASE", "Machine")

# install.ps1 lives next to this script's parent (repo root), regardless of
# whether the user passed -RepoRoot.
$repoRootDir = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$installScript = Join-Path $repoRootDir "install/install.ps1"
if (-not (Test-Path $installScript)) { throw "install.ps1 not found at $installScript" }
if (-not $RepoRoot) { $RepoRoot = $repoRootDir }

# --- locate / publish binaries ---
Check "Prerequisites: binaries" {
    if ($CliDir -and $ServiceDir) {
        Assert-True (Test-Path (Join-Path $CliDir "usenet-backup.exe")) "usenet-backup.exe not found in $CliDir"
        Assert-True (Test-Path (Join-Path $ServiceDir "usenet-backup-service.exe")) "usenet-backup-service.exe not found in $ServiceDir"
    } else {
        Assert-True ($null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)) ".NET SDK not found; install it or pass -CliDir and -ServiceDir."
        # The repo's nuget.config points at a sandbox-only vendored feed
        # (.nuget-local/) that is not shipped. On a normal machine with
        # internet, restore from nuget.org instead.
        $restoreSrc = @()
        if (-not (Test-Path (Join-Path $RepoRoot ".nuget-local"))) {
            $restoreSrc = @("/p:RestoreSources=https://api.nuget.org/v3/index.json")
        }
        $pub = Join-Path $WorkRoot "publish"
        Write-Host "Publishing CLI..."
        dotnet publish "$RepoRoot/src/UsenetBackup.Cli/UsenetBackup.Cli.csproj" -c Release -r win-x64 `
            --self-contained -p:PublishSingleFile=true -o "$pub/cli" @restoreSrc | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }
        Write-Host "Publishing service..."
        dotnet publish "$RepoRoot/src/UsenetBackup.Service/UsenetBackup.Service.csproj" -c Release -r win-x64 `
            --self-contained -p:PublishSingleFile=true -o "$pub/service" @restoreSrc | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "service publish failed" }
        Set-Variable -Name CliDir -Value "$pub/cli" -Scope Script
        Set-Variable -Name ServiceDir -Value "$pub/service" -Scope Script
    }
}
if (@($results | Where-Object { $_.Result -eq "FAIL" }).Count -gt 0) { Report; exit 1 }

$cli = Join-Path $CliDir "usenet-backup.exe"
$work = Join-Path $WorkRoot ([guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$repo = Join-Path $work "repo"

try {
    Check "CLI: init repository" {
        & $cli init $repo --chunk-size 65536 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "init failed"
    }

    Check "VSS: backup --vss reads an exclusively-locked file" {
        $src = New-Item -ItemType Directory -Force -Path (Join-Path $work "vss-src")
        "hello" | Out-File (Join-Path $src "a.txt") -Encoding utf8
        "locked-content" | Out-File (Join-Path $src "locked.txt") -Encoding utf8
        $shadowsBefore = @(Get-CimInstance Win32_ShadowCopy | Select-Object -ExpandProperty ID)

        $lockStream = [IO.File]::Open((Join-Path $src "locked.txt"),
            [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            # A plain backup must NOT be able to read the locked file...
            & $cli backup $repo $src 2>$null | Out-Null
            Assert-True ($LASTEXITCODE -ne 0) "plain backup unexpectedly succeeded on an exclusively-locked file"
            # ...while a VSS backup must sail through via the snapshot.
            $out = & $cli backup $repo $src --vss 2>&1
            Assert-True ($LASTEXITCODE -eq 0) "backup --vss failed: $out"
            $vssBackupId = Get-BackupId ($out | Out-String)
        } finally {
            $lockStream.Close()
        }

        $manifest = Get-Content (Join-Path $repo "manifests" "$vssBackupId.json") -Raw |
            ConvertFrom-Json
        Assert-True ($manifest.snapshot -eq "vss") "manifest snapshot='$($manifest.snapshot)', expected 'vss'"

        & $cli restore $repo $vssBackupId (Join-Path $work "vss-restored") 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "restore of VSS backup failed"
        $content = (Get-Content (Join-Path $work "vss-restored" "locked.txt") -Raw).Trim()
        Assert-True ($content -eq "locked-content") "restored locked file content mismatch: '$content'"

        Start-Sleep -Seconds 5  # give VSS a moment to report deletions
        $shadowsAfter = @(Get-CimInstance Win32_ShadowCopy | Select-Object -ExpandProperty ID)
        $leaked = @($shadowsAfter | Where-Object { $_ -notin $shadowsBefore })
        Assert-True ($leaked.Count -eq 0) "leaked shadow copies: $($leaked -join ', ')"
    }

    Check "Disk image: backup-disk / restore-disk round-trip" {
        $disk = Join-Path $work "disk.bin"
        $target = Join-Path $work "target.bin"
        $size = 3 * 1024 * 1024 + 12345  # not a multiple of the chunk size
        $bytes = New-Object byte[] $size
        # RandomNumberGenerator.Fill is .NET Core 3.0+; use the instance API
        # for Windows PowerShell 5.1 (.NET Framework) compatibility.
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        [IO.File]::WriteAllBytes($disk, $bytes)
        $h1 = (Get-FileHash $disk -Algorithm SHA256).Hash

        $out = & $cli backup-disk $repo $disk --image-name test.img 2>&1
        Assert-True ($LASTEXITCODE -eq 0) "backup-disk failed: $out"
        $id = Get-BackupId ($out | Out-String)
        $manifest = Get-Content (Join-Path $repo "manifests" "$id.json") -Raw | ConvertFrom-Json
        Assert-True ($manifest.kind -eq "disk-image") "manifest kind='$($manifest.kind)', expected 'disk-image'"

        [IO.File]::WriteAllBytes($target, (New-Object byte[] $size))
        & $cli restore-disk $repo $id $target --yes 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "restore-disk failed"
        $h2 = (Get-FileHash $target -Algorithm SHA256).Hash
        Assert-True ($h1 -eq $h2) "disk image hash mismatch after round-trip"

        & $cli verify $repo $id 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "verify of disk-image backup failed"
    }

    Check "Service: install, dashboard run, uninstall" {
        $svcName = "UsenetBackupValidate"
        $svcWork = Join-Path $work "svctest"
        $svcRepo = Join-Path $svcWork "repo"
        $svcSrc = New-Item -ItemType Directory -Force -Path (Join-Path $svcWork "src")
        "svc-data" | Out-File (Join-Path $svcSrc "f.txt") -Encoding utf8
        & $cli init $svcRepo --chunk-size 65536 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "service test repo init failed"

        $port = 15795
        $cfg = @{
            dashboardBind = "127.0.0.1"; dashboardPort = $port
            jobs = @(@{
                name = "validate"; repo = $svcRepo; source = $svcSrc.FullName
                schedule = "interval 60"; mode = "incremental"; vss = $false
            })
        } | ConvertTo-Json -Depth 4
        $cfgPath = Join-Path $svcWork "service.json"
        $cfg | Out-File $cfgPath -Encoding utf8

        try {
            & $installScript -Source $ServiceDir -Config $cfgPath `
                -InstallDir (Join-Path $svcWork "install") `
                -ServiceName $svcName -Passphrase $secPw
            if ($LASTEXITCODE -ne 0) { throw "service install failed" }

            $status = $null
            for ($i = 0; $i -lt 30 -and -not $status; $i++) {
                try { $status = Invoke-RestMethod "http://127.0.0.1:$port/api/status" -TimeoutSec 3 }
                catch { Start-Sleep -Seconds 2 }
            }
            Assert-True ($null -ne $status) "dashboard did not come up at http://127.0.0.1:$port/"
            Assert-True ((@($status.jobs | Where-Object { $_.name -eq "validate" })).Count -eq 1) `
                "job 'validate' missing from dashboard"

            # Dashboard POSTs require the per-startup CSRF token (embedded in
            # the served HTML as <meta name="csrf-token">).
            $page = Invoke-WebRequest "http://127.0.0.1:$port/" -TimeoutSec 10
            $token = [regex]::Match($page.Content, 'name="csrf-token" content="([^"]+)"').Groups[1].Value
            Assert-True ($token.Length -gt 0) "could not extract CSRF token from dashboard HTML"
            Invoke-RestMethod -Method Post "http://127.0.0.1:$port/api/jobs/validate/run" `
                -Headers @{ "X-CSRF-Token" = $token } -TimeoutSec 10 | Out-Null
            $ok = $false
            for ($i = 0; $i -lt 40 -and -not $ok; $i++) {
                Start-Sleep -Seconds 3
                $s = Invoke-RestMethod "http://127.0.0.1:$port/api/status" -TimeoutSec 5
                $lr = ($s.jobs | Where-Object { $_.name -eq "validate" }).lastResult
                if ($lr -and $lr.backupId) {
                    if (-not $lr.success) { throw "service job failed: $($lr.error)" }
                    $ok = $true
                }
            }
            Assert-True $ok "service job did not complete in time"

            $list = & $cli list $svcRepo 2>&1
            Assert-True ($LASTEXITCODE -eq 0 -and $list -match "full") "expected a backup in list output: $list"
        } finally {
            Write-Host "Uninstalling validation service..."
            sc.exe stop $svcName 2>$null | Out-Null
            Start-Sleep -Seconds 2
            sc.exe delete $svcName 2>$null | Out-Null
            $gone = $true
            try { sc.exe query $svcName 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { $gone = $false } } catch {}
            Assert-True $gone "service $svcName still present after uninstall"
        }
    }
} finally {
    Write-Host "`nCleaning up $work ..."
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    # Restore the machine-level passphrase to its pre-validation value.
    [Environment]::SetEnvironmentVariable("USENETBACKUP_PASSPHRASE", $prevMachinePw, "Machine")
}

Report
