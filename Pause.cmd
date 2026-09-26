@echo off
setlocal EnableExtensions

rem ============================================================
rem  FastSeedRoll  --  pause / resume the background search.
rem
rem  The mod runs AUTOMATICALLY by default (no switch needed).
rem  This script is only the escape hatch: it creates/removes
rem  DISABLE_SEARCH.txt, which makes the mod stop searching.
rem
rem  ASCII ONLY. Do NOT put non-ASCII characters in this file:
rem  cmd.exe parses .cmd with the system OEM codepage, so UTF-8
rem  text gets mangled and breaks the batch parser.
rem ============================================================

set "DIR=%APPDATA%\SlayTheSpire2\FastSeedRoll"
set "FLAG=%DIR%\DISABLE_SEARCH.txt"

if not exist "%DIR%" mkdir "%DIR%" >nul 2>&1

if exist "%FLAG%" goto RESUME
goto PAUSE

:PAUSE
> "%FLAG%" echo disabled
echo.
echo    [ PAUSED ]  background search is now DISABLED
echo.
goto DONE

:RESUME
del "%FLAG%" >nul 2>&1
echo.
echo    [ RUNNING ] background search is ENABLED (this is the default)
echo.
goto DONE

:DONE
echo    Flag file : %FLAG%
echo    Reserve   : %DIR%\seeds_found.json
echo    Results   : %DIR%\seeds.txt
echo    Log       : %DIR%\fastseedroll.log
echo.
echo    Return to the game's main menu to take effect (no restart needed).
echo.
pause
endlocal
