@echo off
rem Installs GreekPlot KAEK Importer for the current user (no administrator rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 pause
