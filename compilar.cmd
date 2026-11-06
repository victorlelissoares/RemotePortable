@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
  echo.
  echo Falha na compilacao. Verifique a mensagem acima.
  pause
  exit /b 1
)
echo.
echo Arquivos prontos na pasta publish.
pause
