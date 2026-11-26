@echo off
setlocal

echo Installing .NET...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0dotnet-install.ps1" -Channel 10.0 -Runtime dotnet -Architecture x64 -InstallDir "%~dp0..\.dotnet"
if errorlevel 1 exit /b 1
if /i "%~1"=="--no-launch" exit /b 0

echo Starting application...
start "" "%~dp0..\JeekLauncher.exe"

endlocal
