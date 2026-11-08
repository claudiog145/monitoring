@echo off
rem Doble clic para abrir el menu de la demo (lanzar problemas por empresa).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo-incidents.ps1" %*
pause
