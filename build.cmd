@echo off
cd /d "%~dp0"
dotnet publish src\EditExcel\EditExcel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
if errorlevel 1 (echo Build failed. Install the .NET 10 SDK and retry. & pause & exit /b 1)
echo Ready: publish\win-x64\EditExcel.exe
pause
