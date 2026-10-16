@echo off
rem Wrapper for set-region.ps1 (default: Netherlands, W. Europe Standard Time; -WhatIf to preview): requests administrator rights (UAC).
setlocal
fltmc >nul 2>&1 || goto :elevate

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-region.ps1" %*
set "rc=%ERRORLEVEL%"
rem Keep the window open when started by double-click or after elevation (cmd /c).
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && pause
exit /b %rc%

:elevate
echo Requesting administrator rights...
rem Path and arguments go through environment variables: quotes and apostrophes in them would break
rem a PowerShell string literal. -Wait -PassThru returns the elevated script's exit code to the caller.
set "SELF=%~f0"
set "ARGS=%*"
if defined ARGS (
    powershell -NoProfile -Command "$p = Start-Process -FilePath $env:SELF -ArgumentList $env:ARGS -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
) else (
    powershell -NoProfile -Command "$p = Start-Process -FilePath $env:SELF -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
)
exit /b %ERRORLEVEL%
