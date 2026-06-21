@echo off
rem Builds then launches Jeek Launcher. Usage: Run.cmd [Debug|Release]
setlocal
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

echo Building (%CONFIG%)...
dotnet build "%~dp0JeekLauncher\JeekLauncher.csproj" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

rem Launch the GUI detached so this console can close.
start "" "%~dp0bin\JeekLauncher.exe"
