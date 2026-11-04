@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo Install the .NET 8 SDK or newer from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
dotnet build "src\LetMeSleep\LetMeSleep.csproj" --configuration Release --nologo
if errorlevel 1 (
    pause
    exit /b 1
)
powershell.exe -NoProfile -Command "Start-Process -FilePath 'src\LetMeSleep\bin\Release\net8.0-windows\LetMeSleep.exe' -Verb RunAs"
if errorlevel 1 (
    echo LetMeSleep could not start. Administrator approval is required.
    pause
    exit /b 1
)
