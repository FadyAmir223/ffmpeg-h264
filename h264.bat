@echo off
setlocal EnableExtensions EnableDelayedExpansion

title FFmpeg Old Receiver Converter

echo.
echo ========================================
echo     FFmpeg Old Receiver Converter
echo ========================================
echo.
echo Drag and drop the INPUT folder here,
echo then press ENTER.
echo.

set /p "INPUT="

if "%INPUT%"=="" (
    echo.
    echo No input folder was provided.
    pause
    exit /b
)

rem Remove quotes added by Windows when dragging a folder
set "INPUT=%INPUT:"=%"

if not exist "%INPUT%\." (
    echo.
    echo ERROR: Input folder does not exist.
    pause
    exit /b
)

echo.
echo Drag and drop the OUTPUT folder here,
echo then press ENTER.
echo.

set /p "OUTPUT="

if "%OUTPUT%"=="" (
    echo.
    echo No output folder was provided.
    pause
    exit /b
)

rem Remove quotes added by Windows when dragging a folder
set "OUTPUT=%OUTPUT:"=%"

if not exist "%OUTPUT%\." (
    echo.
    echo Output folder does not exist.
    echo.
    echo Create the output folder first, then run the script again.
    pause
    exit /b
)

if not exist "%~dp0ffmpeg.exe" (
    echo.
    echo ERROR: ffmpeg.exe was not found next to this BAT file.
    echo.
    echo Make sure the folder contains:
    echo     convert.bat
    echo     ffmpeg.exe
    echo.
    pause
    exit /b
)

echo.
echo ========================================
echo Starting conversion...
echo ========================================
echo.

for %%F in ("%INPUT%\*") do (
    if exist "%%~fF" (
        set "EXT=%%~xF"

        if /I "!EXT!"==".mp4" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".mkv" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".webm" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".avi" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".mov" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".flv" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".ts" (
            call :CONVERT "%%~fF" "%%~nF"
        ) else if /I "!EXT!"==".m4v" (
            call :CONVERT "%%~fF" "%%~nF"
        )
    )
)

echo.
echo ========================================
echo All conversions finished.
echo ========================================
echo.

pause
exit /b


:CONVERT

echo.
echo ----------------------------------------
echo Converting:
echo %~nx1
echo ----------------------------------------
echo.

"%~dp0ffmpeg.exe" -i "%~1" -vf "scale=800:480:force_original_aspect_ratio=decrease,pad=800:480:(ow-iw)/2:(oh-ih)/2" -c:v libx264 -profile:v baseline -level:v 2.0 -pix_fmt yuv420p -r 30000/1001 -b:v 2200k -c:a aac -profile:a aac_low -b:a 128k -ar 44100 -ac 2 "%OUTPUT%\%~2.mp4"

if errorlevel 1 (
    echo.
    echo ERROR converting:
    echo %~nx1
) else (
    echo.
    echo DONE:
    echo %OUTPUT%\%~2.mp4
)

exit /b