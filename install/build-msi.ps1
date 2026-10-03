# Builds the Usenet Backup MSI.
# Generates a deterministic harvest fragment (explicit unique IDs, stable GUIDs
# derived from install paths) for the three self-contained publish outputs,
# then runs wix build. Run on Windows with the WiX v5 dotnet tool installed.
#
# Example:
#   .\install\build-msi.ps1 -SourceDir C:\ub\src `
#       -CliBin C:\ub\publish\cli -ServiceBin C:\ub\publish\service `
#       -RecoveryBin C:\ub\publish\recovery `
#       -OutputMsi C:\ub\usenet-backup-0.8.0-x64.msi

param(
    [string]$SourceDir = "C:\ub\src",
    [string]$CliBin = "C:\ub\publish\cli",
    [string]$ServiceBin = "C:\ub\publish\service",
    [string]$RecoveryBin = "C:\ub\publish\recovery",
    [string]$OutputMsi = "C:\ub\usenet-backup-0.8.0-x64.msi",
    [string]$ProductVersion = "0.8.0"
)

$ErrorActionPreference = "Stop"
function Fail($msg) { Write-Error $msg; exit 1 }

foreach ($d in @($CliBin, $ServiceBin, $RecoveryBin)) {
    if (-not (Test-Path $d)) { Fail "Publish dir not found: $d" }
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { Fail "wix not found. dotnet tool install --global wix --version 5.0.2" }

function Get-StableGuid([string]$seed) {
    $bytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash(
        [System.Text.Encoding]::UTF8.GetBytes($seed))
    return [Guid]::new($bytes[0..15]).ToString().ToUpper()
}
function Esc([string]$s) { return [System.Security.SecurityElement]::Escape($s) }

function Emit-DirSubtree(
    [System.Text.StringBuilder]$Xml,
    [hashtable]$DirNodes,
    [hashtable]$Children,
    [string]$Id,
    [int]$Depth) {
    $indent = "  " * $Depth
    $node = $DirNodes[$Id]
    [void]$Xml.AppendLine(('{0}<Directory Id="{1}" Name="{2}">' -f $indent, $Id, (Esc $node.Name)))
    if ($Children.ContainsKey($Id)) {
        foreach ($kid in ($Children[$Id] | Sort-Object)) {
            Emit-DirSubtree $Xml $DirNodes $Children $kid ($Depth + 1)
        }
    }
    [void]$Xml.AppendLine(('{0}</Directory>' -f $indent))
}

$xml = New-Object System.Text.StringBuilder
[void]$xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$xml.AppendLine('  <Fragment>')

# $apps: group -> @{ Bin=...; DirId=... }  (DirIds are defined in UsenetBackup.wxs)
$apps = @(
    @{ Group = "Cli";      Bin = $CliBin;      DirId = "CLIDIR" },
    @{ Group = "Service";  Bin = $ServiceBin;  DirId = "SERVICEDIR" },
    @{ Group = "Recovery"; Bin = $RecoveryBin; DirId = "WIZARDDIR" }
)

foreach ($app in $apps) {
    $group = $app.Group
    $bin = (Resolve-Path $app.Bin).Path
    $files = Get-ChildItem -Path $bin -File -Recurse | Sort-Object FullName
    if ($group -eq "Service") {
        # The service exe has its own explicit component (ServiceInstall must share
        # a component with its executable); harvest everything else.
        $files = $files | Where-Object { $_.Name -ne "usenet-backup-service.exe" }
    }

    # Map relative dir -> directory id, then emit nested Directory elements.
    # $dirIds: relDir -> id ; $dirNodes: id -> @{ Name=; Parent= }
    $dirIds = @{ "" = $app.DirId }
    $dirNodes = @{}
    $dirIndex = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($bin.Length).TrimStart('\', '/')
        $relDir = Split-Path $rel -Parent
        if ($null -eq $relDir) { $relDir = "" }
        if (-not $dirIds.ContainsKey($relDir)) {
            $parts = if ($relDir -eq "") { @() } else { $relDir -split '[\\/]' }
            $accum = ""
            $parentId = $app.DirId
            foreach ($part in $parts) {
                $accum = if ($accum -eq "") { $part } else { "$accum\$part" }
                if (-not $dirIds.ContainsKey($accum)) {
                    $id = "d_{0}_{1}" -f $group.ToLower(), $dirIndex++
                    $dirIds[$accum] = $id
                    $dirNodes[$id] = @{ Name = $part; Parent = $parentId }
                }
                $parentId = $dirIds[$accum]
            }
        }
    }

    # Emit one DirectoryRef per app root containing its subdirectory tree.
    $children = @{}
    foreach ($kv in $dirNodes.GetEnumerator()) {
        $p = $kv.Value.Parent
        if (-not $children.ContainsKey($p)) { $children[$p] = @() }
        $children[$p] += $kv.Key
    }
    if ($children.ContainsKey($app.DirId)) {
        [void]$xml.AppendLine(('    <DirectoryRef Id="{0}">' -f $app.DirId))
        foreach ($id in ($children[$app.DirId] | Sort-Object)) {
            Emit-DirSubtree $xml $dirNodes $children $id 3
        }
        [void]$xml.AppendLine('    </DirectoryRef>')
    }

    # Emit one component per file, ids unique per group, GUID stable per install path.
    [void]$xml.AppendLine(('    <ComponentGroup Id="{0}Files">' -f $group))
    $i = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($bin.Length).TrimStart('\', '/')
        $relDir = Split-Path $rel -Parent
        if ($null -eq $relDir) { $relDir = "" }
        $dirId = $dirIds[$relDir]
        $cid = "c_{0}_{1}" -f $group.ToLower(), $i
        $fid = "f_{0}_{1}" -f $group.ToLower(), $i
        $i++
        # Install path seed: UsenetBackup\<appdir>\<relpath> — stable across builds.
        $seed = "UsenetBackup\$($app.DirId)\$rel"
        $guid = Get-StableGuid $seed
        [void]$xml.AppendLine(('      <Component Id="{0}" Guid="{1}" Directory="{2}" Bitness="always64">' -f $cid, $guid, $dirId))
        [void]$xml.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="true" />' -f $fid, (Esc $f.FullName)))
        [void]$xml.AppendLine('      </Component>')
    }
    [void]$xml.AppendLine('    </ComponentGroup>')
}

[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('</Wix>')

$fragmentPath = Join-Path $PSScriptRoot "_harvest.wxs"
$xml.ToString() | Out-File -FilePath $fragmentPath -Encoding utf8 -Force
Write-Host "Harvest fragment written: $fragmentPath"

# Build from the source dir so the relative doc paths in UsenetBackup.wxs resolve.
Push-Location $SourceDir
try {
    & wix build -arch x64 `
        -d ServiceBin=$ServiceBin `
        -d ProductVersion=$ProductVersion `
        -o $OutputMsi `
        install/UsenetBackup.wxs install/_harvest.wxs
    if ($LASTEXITCODE -ne 0) { Fail "wix build failed." }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host ("Done: {0} ({1:N0} bytes)" -f $OutputMsi, (Get-Item $OutputMsi).Length)
