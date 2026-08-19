@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\ColorPanel14.ps1"
if errorlevel 1 (
  echo.
  echo Exit code %errorlevel%
  echo If this crashed, create an empty file called  noccd.txt  in this folder
  echo and run again - that disables the monitor-naming interop.
  echo.
  pause
)
