@echo off
rem Lemon Paperplane: one-step Windows build (runs build.ps1 regardless of the PowerShell execution policy)
rem From PowerShell 7 the inherited PSModulePath makes Windows PowerShell 5.1 load the wrong modules (Get-FileHash etc. missing):
rem clear it so 5.1 rebuilds its own default.
set "PSModulePath="
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
