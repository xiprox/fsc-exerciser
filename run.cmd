@echo off
rem Builds the exerciser if anything changed, then starts it on its own, so no console is left
rem standing behind the window. Arguments are passed through: run --relay localhost --join CODE
setlocal
cd /d "%~dp0"

if not exist "fsc\FsCopilot.dll" (
    echo There is no FS Copilot build under fsc\ yet. Sync one first, once:
    echo.
    echo   powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1
    echo.
    echo It asks which FS Copilot checkout to sync from.
    echo.
    pause
    exit /b 1
)

rem This build is the only thing standing between a synced copy whose API has moved and a
rem window that dies on its first line. It is load-bearing, so it has to actually run: see
rem the StampSyncedBuild target, which is what makes a changed copy look changed to MSBuild.
dotnet build src\FsCopilot.Exerciser.csproj -c Debug --nologo -v q
if errorlevel 1 (
    echo.
    echo The exerciser will not build against the FS Copilot build under fsc\:
    echo.
    type fsc-exerciser.json
    echo.
    echo If the errors above name missing types or members, the copy under fsc\ does not have
    echo the FS Copilot work this exerciser drives. Either that checkout is the wrong one, or
    echo its bin\ was built before the source that has it - a sync copies the build, not the
    echo source, so an FS Copilot nobody rebuilt syncs across looking perfectly valid.
    echo.
    echo Build FS Copilot in that checkout, then:
    echo.
    echo   powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1
    echo.
    echo Or point the sync somewhere else:
    echo.
    echo   powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1 -Fsc ^<checkout^>
    echo.
    echo If they name a file that is in use, an exerciser is already running. Close it.
    echo.
    pause
    exit /b 1
)

start "" "src\bin\Debug\net9.0\win-x64\FsCopilot.Exerciser.exe" %*
