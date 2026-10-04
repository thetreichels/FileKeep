# Smoke-tests the Usenet Backup dashboard API: Settings UI endpoints.
# Run on the VM after installing the service and starting it.
# Usage: .\smoke-dashboard.ps1 [-Port 15789]
#
# Tests:
#   1. GET /api/config returns jobs + nntp
#   2. POST /api/config/jobs creates a job (backupPrivilege + autoUpload round-trip)
#   3. POST /api/config/jobs edits a job
#   4. POST /api/config/nntp saves Usenet provider (password -> DPAPI blob, never returned)
#   5. GET /api/config confirms password blob not exposed, hasPassword=true
#   6. DELETE /api/config/jobs removes the test job
#   7. DELETE refuses to remove the last job
#   8. service.json on disk matches the API state

param([int]$Port = 15789)

$ErrorActionPreference = "Stop"
$base = "http://127.0.0.1:$Port"
$passed = 0
$failed = 0

function Check($name, [bool]$ok, $detail = "") {
    if ($ok) { Write-Host "  PASS: $name" -ForegroundColor Green; $script:passed++ }
    else { Write-Host "  FAIL: $name $detail" -ForegroundColor Red; $script:failed++ }
}

Write-Host "Fetching dashboard HTML for CSRF token..." -ForegroundColor Cyan
$html = Invoke-WebRequest -Uri "$base/" -UseBasicParsing
if ($html.Content -match '<meta name="csrf-token" content="([^"]+)"') {
    $csrf = $Matches[1]
    Write-Host "  CSRF token acquired." -ForegroundColor Green
} else {
    Write-Host "  FAIL: Could not find CSRF token in dashboard HTML" -ForegroundColor Red
    exit 1
}
$headers = @{ "X-CSRF-Token" = $csrf }

Write-Host "`n[1] GET /api/config" -ForegroundColor Cyan
$config = Invoke-RestMethod -Uri "$base/api/config"
Check "config has jobs" ($config.jobs.Count -ge 1)
Check "config has nntp property" ($null -ne $config.PSObject.Properties["nntp"])
$initialJobCount = $config.jobs.Count
Write-Host "  Jobs: $($config.jobs.Count), NNTP host: '$($config.nntp.host)'"

Write-Host "`n[2] POST /api/config/jobs (create)" -ForegroundColor Cyan
$newJob = @{
    name = "smoke-test-job"
    source = "C:\Windows\Temp"
    repo = "C:\Windows\Temp\ub-smoke-repo"
    schedule = "interval 60"
    mode = "incremental"
    backupPrivilege = $true
    autoUpload = $true
} | ConvertTo-Json
# autoUpload=true requires an NNTP provider; set a dummy one first if none exists
if ([string]::IsNullOrWhiteSpace($config.nntp.host)) {
    Write-Host "  (setting dummy NNTP provider first, required for autoUpload)" -ForegroundColor Yellow
    $dummyNntp = @{ host = "news.example.com"; port = 119; username = ""; password = "";
                    ssl = $false; connections = 2 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$base/api/config/nntp" -Method Post -Headers $headers `
        -ContentType "application/json" -Body $dummyNntp | Out-Null
}
try {
    $created = Invoke-RestMethod -Uri "$base/api/config/jobs" -Method Post -Headers $headers `
        -ContentType "application/json" -Body $newJob
    Check "job created" ($created.name -eq "smoke-test-job")
} catch {
    Check "job created" $false $_.Exception.Message
}

Write-Host "`n[3] Verify backupPrivilege/autoUpload round-trip via GET" -ForegroundColor Cyan
$config = Invoke-RestMethod -Uri "$base/api/config"
$job = $config.jobs | Where-Object { $_.name -eq "smoke-test-job" }
Check "backupPrivilege=true round-tripped" ($job.backupPrivilege -eq $true)
Check "autoUpload=true round-tripped" ($job.autoUpload -eq $true)

Write-Host "`n[4] POST /api/config/jobs (edit: disable flags)" -ForegroundColor Cyan
$editJob = @{
    name = "smoke-test-job"
    source = "C:\Windows\Temp"
    repo = "C:\Windows\Temp\ub-smoke-repo"
    schedule = "daily 03:00"
    mode = "full"
    backupPrivilege = $false
    autoUpload = $false
} | ConvertTo-Json
Invoke-RestMethod -Uri "$base/api/config/jobs" -Method Post -Headers $headers `
    -ContentType "application/json" -Body $editJob | Out-Null
$config = Invoke-RestMethod -Uri "$base/api/config"
$job = $config.jobs | Where-Object { $_.name -eq "smoke-test-job" }
Check "schedule updated" ($job.schedule -eq "daily 03:00")
Check "mode updated" ($job.mode -eq "full")
Check "flags cleared" ($job.backupPrivilege -eq $false -and $job.autoUpload -eq $false)

Write-Host "`n[5] POST /api/config/nntp (save provider + password)" -ForegroundColor Cyan
$nntp = @{
    host = "news.example.com"
    port = 563
    username = "smokeuser"
    password = "smoke-test-password-123"
    ssl = $true
    connections = 2
} | ConvertTo-Json
$result = Invoke-RestMethod -Uri "$base/api/config/nntp" -Method Post -Headers $headers `
    -ContentType "application/json" -Body $nntp
Check "NNTP host saved" ($result.host -eq "news.example.com")
Check "response has hasPassword=true" ($result.hasPassword -eq $true)
Check "response does NOT expose blob" ($null -eq $result.PSObject.Properties["passwordProtected"])

Write-Host "`n[6] GET /api/config confirms blob not exposed" -ForegroundColor Cyan
$config = Invoke-RestMethod -Uri "$base/api/config"
Check "nntp.host present" ($config.nntp.host -eq "news.example.com")
Check "nntp.hasPassword=true" ($config.nntp.hasPassword -eq $true)
Check "passwordProtected NOT in API response" ($null -eq $config.nntp.PSObject.Properties["passwordProtected"])
Check "plaintext password NOT in API response" ($null -eq $config.nntp.PSObject.Properties["password"])

Write-Host "`n[7] service.json on disk has the DPAPI blob" -ForegroundColor Cyan
$svcPath = "C:\Program Files\UsenetBackup\service\service.json"
if (Test-Path $svcPath) {
    $diskJson = Get-Content $svcPath -Raw | ConvertFrom-Json
    Check "disk config has passwordProtected blob" `
        (-not [string]::IsNullOrWhiteSpace($diskJson.nntp.passwordProtected))
    Check "disk config has NO plaintext password" `
        ($null -eq $diskJson.nntp.PSObject.Properties["password"])
    $diskJob = $diskJson.jobs | Where-Object { $_.name -eq "smoke-test-job" }
    Check "disk config has test job" ($null -ne $diskJob)
} else {
    Check "service.json exists at $svcPath" $false "(path not found)"
}

Write-Host "`n[8] Scheduler reloaded (check /api/status)" -ForegroundColor Cyan
$status = Invoke-RestMethod -Uri "$base/api/status"
$sjob = $status.jobs | Where-Object { $_.name -eq "smoke-test-job" }
Check "scheduler knows the new job" ($null -ne $sjob)

Write-Host "`n[9] DELETE /api/config/jobs (remove test job)" -ForegroundColor Cyan
Invoke-RestMethod -Uri "$base/api/config/jobs/smoke-test-job" -Method Delete -Headers $headers | Out-Null
$config = Invoke-RestMethod -Uri "$base/api/config"
Check "test job deleted" (($config.jobs | Where-Object { $_.name -eq "smoke-test-job" }).Count -eq 0)
Check "job count back to initial" ($config.jobs.Count -eq $initialJobCount)

Write-Host "`n[10] DELETE refuses to remove the last job" -ForegroundColor Cyan
# Delete all but one, then try to delete the last
while ((Invoke-RestMethod -Uri "$base/api/config").jobs.Count -gt 1) {
    $c = Invoke-RestMethod -Uri "$base/api/config"
    $victim = $c.jobs[0].name
    Invoke-RestMethod -Uri "$base/api/config/jobs/$victim" -Method Delete -Headers $headers | Out-Null
}
$lastName = (Invoke-RestMethod -Uri "$base/api/config").jobs[0].name
try {
    Invoke-RestMethod -Uri "$base/api/config/jobs/$lastName" -Method Delete -Headers $headers `
        -ErrorAction Stop | Out-Null
    Check "last job delete refused" $false "(API allowed it!)"
} catch {
    # Reaching here means the API refused the delete (non-2xx status).
    # The "one job remains" check below confirms the guard worked.
    Check "last job delete refused" $true
}
$finalCount = (Invoke-RestMethod -Uri "$base/api/config").jobs.Count
Check "one job remains" ($finalCount -eq 1)

Write-Host "`n==============================" -ForegroundColor Cyan
$color = "Red"
if ($failed -eq 0) { $color = "Green" }
Write-Host "  Passed: $passed   Failed: $failed" -ForegroundColor $color
Write-Host "=============================="
if ($failed -eq 0) { exit 0 } else { exit 1 }
