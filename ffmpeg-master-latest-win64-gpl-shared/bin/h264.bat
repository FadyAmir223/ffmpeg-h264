@echo off
setlocal
title FFmpeg Old Receiver Converter

set "powershell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%powershell%" (
    echo Windows PowerShell was not found at its standard location:
    echo %powershell%
    echo.
    pause
    exit /b 1
)

"%powershell%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0h264.ps1"
set "result=%errorlevel%"

echo.
pause
exit /b %result%
