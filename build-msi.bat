@echo off
cd c:\ub\src
set DOTNET_ROOT=c:\ub\dotnet
set PATH=c:\ub\dotnet;%PATH%
wix build -arch x64 -d SrcRoot=c:\ub\src -d CliBin=c:\ub\publish\cli -d ServiceBin=c:\ub\publish\service -d RecoveryBin=c:\ub\publish\recovery -o c:\ub\usenet-backup-0.8.1-x64.msi install\usenetbackup.wxs
echo.
echo Build done. MSI size:
dir c:\ub\usenet-backup-0.8.1-x64.msi | findstr "usenet-backup"
pause
