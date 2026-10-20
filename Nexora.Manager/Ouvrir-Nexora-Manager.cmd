@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned -File "%~dp0Nexora-Manager.ps1"
if errorlevel 1 (
 echo.
 echo Nexora Manager n'a pas pu demarrer.
 echo Verifiez si les scripts sont bloques par Windows.
 echo Dans PowerShell apres verification: Get-ChildItem .\Nexora.Manager\*.ps1 ^| Unblock-File
 pause
)
