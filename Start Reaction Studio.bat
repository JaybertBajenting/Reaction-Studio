@echo off
REM === Reaction Studio (.NET app) launcher ===
REM Runs the already-built app instantly. If it's missing, builds it first.
set "EXE=%~dp0App\bin\Debug\net10.0-windows\ReactionStudio.exe"

if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo Building Reaction Studio for the first time...
    dotnet build "%~dp0App\ReactionStudio.csproj" -v minimal
    start "" "%EXE%"
)
