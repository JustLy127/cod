@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Manage-Shortcuts.ps1" -Action Install
if errorlevel 1 pause
