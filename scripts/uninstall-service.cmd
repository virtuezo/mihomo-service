@echo off
rem Wrapper for uninstall-service.ps1: requests administrator rights (UAC), bypasses ExecutionPolicy.
setlocal
fltmc >nul 2>&1 || goto :elevate

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1" %*
set "rc=%ERRORLEVEL%"
rem Keep the window open when started by double-click or after elevation (cmd /c).
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && pause
exit /b %rc%

:elevate
echo Requesting administrator rights...
if "%~1"=="" (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
) else (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
)
exit /b %ERRORLEVEL%
