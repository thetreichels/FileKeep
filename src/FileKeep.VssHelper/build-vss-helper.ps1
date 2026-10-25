# Builds FileKeepVss.exe — the native VSS shadow copy helper.
#
# Tries MSVC (Visual Studio Build Tools) first, falls back to MinGW-w64.
# Run on Windows:
#   powershell -ExecutionPolicy Bypass -File build-vss-helper.ps1
#
# Output: FileKeepVss.exe in the script directory.

$ErrorActionPreference = "Stop"

$srcDir = $PSScriptRoot
$outExe = Join-Path $srcDir "FileKeepVss.exe"
$cppFile = Join-Path $srcDir "FileKeepVss.cpp"

# Try MSVC first
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msvcFound = $false
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($vsPath) {
        $vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
        if (Test-Path $vcvars) {
            Write-Host "Using Visual Studio at $vsPath"
            $compileCmd = "`"$vcvars`" >nul 2>&1 && cl.exe /nologo /std:c++17 /MT /O1 /W3 /EHsc " +
                "`"$cppFile`" /link /OUT:`"$outExe`" VssApi.lib"
            Write-Host "Compiling with MSVC..."
            cmd /c $compileCmd
            if ($LASTEXITCODE -eq 0) { $msvcFound = $true }
        }
    }
}

# Fall back to MinGW-w64
if (-not $msvcFound) {
    $mingwGcc = "C:\mingw\mingw64\bin\g++.exe"
    if (-not (Test-Path $mingwGcc)) {
        throw "Neither MSVC nor MinGW-w64 found. Install one of them."
    }
    Write-Host "Using MinGW-w64 at C:\mingw"
    Write-Host "Compiling with MinGW..."
    # -municode for wmain, -static for self-contained exe, -luuid for GUID_NULL
    & $mingwGcc -mconsole -municode -static -O2 -o $outExe $cppFile -lvssapi -lole32 -luuid
    if ($LASTEXITCODE -ne 0) { throw "MinGW compilation failed" }
}

$size = (Get-Item $outExe).Length
Write-Host "Built $outExe ($size bytes)"
Write-Host "Build complete"
