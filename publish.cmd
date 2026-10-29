@echo off
setlocal
pushd "%~dp0" || exit /b 1
dotnet publish HttpFreeKit.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist
if errorlevel 1 goto publish_failed
echo.
echo Standalone executable ready: dist\HTTP FreeKit.exe
pause
popd
exit /b 0

:publish_failed
echo.
echo Publish failed. Review the error messages above.
pause
popd
exit /b 1
