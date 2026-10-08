@echo off
rem Wrapper for build.ps1: bypasses ExecutionPolicy, passes all arguments through.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "rc=%ERRORLEVEL%"
rem Keep the window open when started by double-click (cmd /c).
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && pause
exit /b %rc%
