@echo off
REM ============================================================
REM  JimiRoll  --  upload to Steam Workshop (ASCII ONLY!)
REM
REM  Usage:
REM    1. Put this file NEXT TO ModUploader.exe
REM    2. Make sure the workspace folder (the one containing
REM       workshop.json / content / image.png) exists
REM    3. Run:  upload.cmd  <workspace-folder>
REM       e.g.  upload.cmd  JimiRoll
REM
REM  On the FIRST upload the tool writes mod_id.txt into that
REM  folder. KEEP IT / BACK IT UP -- it is how future uploads
REM  target the SAME workshop item. Lose it and you will create
REM  a brand new item instead of updating.
REM ============================================================

setlocal
if "%~1"=="" (
  echo Usage: upload.cmd ^<workspace-folder^>
  echo Example: upload.cmd JimiRoll
  exit /b 1
)

if not exist "ModUploader.exe" (
  echo [x] ModUploader.exe not found in the current folder.
  echo     Put this .cmd next to ModUploader.exe and run it there.
  exit /b 1
)

if not exist "%~1\workshop.json" (
  echo [x] "%~1\workshop.json" not found. Wrong workspace folder?
  exit /b 1
)

echo [*] uploading workspace "%~1" ...
ModUploader.exe upload -w "%~1"
set RC=%ERRORLEVEL%

echo.
if exist "%~1\mod_id.txt" (
  echo [i] mod_id.txt:
  type "%~1\mod_id.txt"
  echo.
)
if exist "mod-uploader.log" echo [i] log written to mod-uploader.log

echo [*] exit code = %RC%
endlocal & exit /b %RC%
