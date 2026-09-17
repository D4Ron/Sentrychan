@echo off
REM ---------------------------------------------------------------------------
REM  Sentrychan - build-and-run launcher (for the desktop shortcut).
REM
REM  Rebuilds from the CURRENT source every launch, so double-clicking the
REM  desktop icon always runs your latest code. A plain shortcut to the .exe
REM  would keep running whatever was last compiled.
REM
REM  Stopping the running instance first is required: a live process locks its
REM  own DLLs and the rebuild would fail.
REM ---------------------------------------------------------------------------

title Sentrychan launcher

echo [1/3] Stopping any running instance...
taskkill /IM Sentrychan.App.exe /F >nul 2>&1

echo [2/3] Building latest source...
dotnet build "C:\Sentrychan\Sentrychan.App\Sentrychan.App.csproj" -c Debug --nologo -v q
if errorlevel 1 (
    echo.
    echo *** BUILD FAILED - see the errors above. App not started. ***
    echo.
    pause
    exit /b 1
)

echo [3/3] Starting Sentrychan...
start "" "C:\Sentrychan\Sentrychan.App\bin\Debug\net9.0\Sentrychan.App.exe"
exit /b 0
