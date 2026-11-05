@echo off
setlocal
set "PSModulePath="
set "SUSU_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "SUSU_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%SUSU_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Theme.ps1" %*
set "SUSU_EXIT=%errorlevel%"
pause
exit /b %SUSU_EXIT%
