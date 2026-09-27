@echo off
rem Installs Nadlan KAEK Importer for the current user (no administrator rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 pause
