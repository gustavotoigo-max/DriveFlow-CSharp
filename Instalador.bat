@echo off
rem Gera o instalador PRIVADO (com o login do Google embutido). Nunca publique o resultado.
setlocal
cd /d "%~dp0"
if not exist oauth_client.json (
  echo Falta o oauth_client.json nesta pasta. Copie o JSON do cliente OAuth para:
  echo   %CD%\oauth_client.json
  exit /b 1
)
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 nao encontrado em "%ISCC%".
  exit /b 1
)
call Publicar.bat || exit /b 1
findstr /c:"\"login\":true" publish\smoke.json >nul 2>&1
if errorlevel 1 (
  echo O pacote foi gerado sem o login do Google embutido.
  exit /b 1
)
"%ISCC%" installer\DriveFlow.iss || exit /b 1
echo.
echo Instalador pronto na pasta publish. Nao envie para o GitHub.
