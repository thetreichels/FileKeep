# Builds FileKeepVss.exe — the native VSS shadow copy helper.
#
# Requires: Visual Studio Build Tools (MSVC) + Windows SDK
# Run on Windows:
#   powershell -ExecutionPolicy Bypass -File build-vss-helper.ps1
#
# Output: FileKeepVss.exe in the script directory.

$ErrorActionPreference = "Stop"

$srcDir = $PSScriptRoot
$outExe = Join-Path $srcDir "FileKeepVss.exe"

# Find MSVC cl.exe via vswhere
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio Build Tools."
}

$vsPath = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw "No Visual Studio with C++ tools found."
}

# Import the VS environment
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    throw "vcvars64.bat not found at $vcvars"
}

Write-Host "Using Visual Studio at $vsPath"

# Compile: C++17, static runtime (/MT), optimize for size (/O1)
# VSS headers are in the Windows SDK (included via vcvars)
$compileCmd = "`"$vcvars`" >nul 2>&1 && cl.exe /nologo /std:c++17 /MT /O1 /W3 /EHsc " +
    "`"$srcDir\FileKeepVss.cpp`" /link /OUT:`"$outExe`" VssApi.lib"

Write-Host "Compiling FileKeepVss.cpp..."
cmd /c $compileCmd
if ($LASTEXITCODE -ne 0) { throw "Compilation failed" }

$size = (Get-Item $outExe).Length
Write-Host "Built $outExe ($size bytes)"

# Verify it runs (should print usage with --help)
& $outExe --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Smoke test failed" }
Write-Host "Smoke test passed (--help works)"
