@echo off
rem Lemon Paperplane: one-step Windows build (runs build.ps1 regardless of the PowerShell execution policy)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
