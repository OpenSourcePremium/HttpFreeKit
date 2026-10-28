@echo off
dotnet publish HttpFreeKit.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist
echo.
echo Standalone executable ready: dist\HTTP FreeKit.exe
pause
