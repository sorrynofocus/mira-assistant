@echo off
setlocal
cd /d "%~dp0.."

if "%~1"=="" (
  echo Usage: scripts\create-user.cmd ^<username^>
  echo You will be prompted for the password twice. Nothing is echoed.
  exit /b 2
)

dotnet run --project backend\Mira.Api\Mira.Api.csproj --no-launch-profile -- create-user --username "%~1"
exit /b %errorlevel%
