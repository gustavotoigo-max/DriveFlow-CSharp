@echo off
rem Gera o DriveFlow em pasta (abre rápido: sem extração para a pasta temporária).
setlocal
cd /d "%~dp0"
dotnet test || exit /b 1
if exist publish\DriveFlow rmdir /s /q publish\DriveFlow
dotnet publish src\DriveFlow.App -c Release -o publish\DriveFlow || exit /b 1
if exist publish\smoke.json del publish\smoke.json
publish\DriveFlow\DriveFlow.exe --smoke-test "%CD%\publish\smoke.json"
findstr /c:"\"ok\":true" publish\smoke.json >nul 2>&1
if errorlevel 1 (
  if exist publish\smoke.json type publish\smoke.json
  echo O teste rapido do pacote falhou.
  exit /b 1
)
type publish\smoke.json
echo.
echo Pronto: publish\DriveFlow\DriveFlow.exe
