@echo off
setlocal
cd /d "%~dp0.."

if not exist "frontend\node_modules" (
  echo Install frontend dependencies with: npm --prefix frontend ci
  exit /b 1
)

start "MIRA API" cmd /k "dotnet run --project backend\Mira.Api\Mira.Api.csproj --no-launch-profile"
start "MIRA Frontend" cmd /k "cd frontend && npm run dev"
echo API:      http://127.0.0.1:5080/health/live
echo Frontend: http://127.0.0.1:4321/
exit /b 0
