@echo off
setlocal
pushd "%~dp0" || exit /b 1
if not exist "Assets\WinDivert\WinDivert.dll" goto windivert_missing
if not exist "Assets\WinDivert\WinDivert64.sys" goto windivert_missing
if not exist "Assets\WinDivert\LICENSE.txt" goto windivert_missing
if not exist "THIRD-PARTY-NOTICES.md" goto release_files_missing
if not exist "dist" mkdir "dist"
if exist "dist\WinDivert.dll" del /q "dist\WinDivert.dll"
if exist "dist\WinDivert64.sys" del /q "dist\WinDivert64.sys"
dotnet publish HttpFreeKit.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist
if errorlevel 1 goto publish_failed
copy /y "Assets\WinDivert\LICENSE.txt" "dist\WinDivert-LICENSE.txt" >nul
if errorlevel 1 goto publish_failed
copy /y "THIRD-PARTY-NOTICES.md" "dist\THIRD-PARTY-NOTICES.md" >nul
if errorlevel 1 goto publish_failed
powershell.exe -NoProfile -Command "Compress-Archive -Path 'dist\HTTP FreeKit.exe','dist\WinDivert-LICENSE.txt','dist\THIRD-PARTY-NOTICES.md' -DestinationPath ('dist\HTTP FreeKit ' + (Get-Content 'version' -Raw).Trim() + ' portable.zip') -CompressionLevel Optimal -Force"
if errorlevel 1 goto publish_failed
echo.
echo Portable executable ready: dist\HTTP FreeKit.exe
echo Portable release archive created in dist.
echo WinDivert files are embedded and extracted beside the executable when capture starts.
popd
exit /b 0

:publish_failed
echo.
echo Publish failed. Review the error messages above.
popd
exit /b 1

:windivert_missing
echo.
echo WinDivert 2.2.2 x64 binaries are required in Assets\WinDivert.
echo Download WinDivert-2.2.2-A.zip from https://github.com/basil00/WinDivert/releases/tag/v2.2.2
echo Copy WinDivert.dll and WinDivert64.sys from its x64 folder to Assets\WinDivert.
popd
exit /b 1

:release_files_missing
echo.
echo LICENSE and THIRD-PARTY-NOTICES.md are required to prepare the release.
popd
exit /b 1
