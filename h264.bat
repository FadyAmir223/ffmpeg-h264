@echo off
setlocal
title FFmpeg Old Receiver Converter

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0h264.ps1"
set "result=%errorlevel%"

echo.
pause
exit /b %result%
