@echo off
rem Publishes an optimized build (ReadyToRun, nested Runtime\ layout) to .\publish.
rem Usage: Publish.cmd [runtime] [self]
rem   runtime : RID, default win-x64
rem   self    : pass "self" for a self-contained build (no .NET runtime needed on target)
setlocal
set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "SC=--no-self-contained"
if /i "%~2"=="self" set "SC=--self-contained"

set "OUT=%~dp0publish"

echo Publishing Release for %RID% (ReadyToRun, %SC%) -^> "%OUT%"
if exist "%OUT%" rmdir /s /q "%OUT%"

dotnet publish "%~dp0JeekLauncher\JeekLauncher.csproj" -c Release -r %RID% %SC% -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o "%OUT%\Runtime"
if errorlevel 1 (
    echo.
    echo Publish FAILED. If a file is locked, close the published app and retry.
    pause
    exit /b 1
)

rem Include version-controlled runtime files from bin\ (not copied by csproj) and strip PDBs.
xcopy /E /I /Y "%~dp0bin\Data" "%OUT%\Data" >nul
xcopy /E /I /Y "%~dp0bin\Scripts" "%OUT%\Scripts" >nul
del /S /Q "%OUT%\*.pdb" >nul 2>&1

echo.
echo Published -^> "%OUT%"
echo   Executable : %OUT%\JeekLauncher.exe
echo   Runtime    : %OUT%\Runtime
pause
