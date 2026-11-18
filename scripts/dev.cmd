@echo off
setlocal EnableExtensions
cd /d "%~dp0.."

if not exist "frontend\node_modules" (
  echo Install frontend dependencies with: npm --prefix frontend ci
  exit /b 1
)

REM Avoid a pause on npm's update-notifier ("npm notice") while the frontend starts.
set "NPM_CONFIG_UPDATE_NOTIFIER=false"

echo API:      http://127.0.0.1:5080/health/live
echo Frontend: http://127.0.0.1:4321/
echo.
echo The API starts in the background in this window; the frontend runs in the foreground.
echo Press Ctrl+C to stop the frontend. Both share this console, so Ctrl+C may also stop the API.
echo If the API is still listening on port 5080 after the frontend stops, end it with:
echo   for /f "tokens=5" %%P in ('netstat -ano ^| findstr /C:":5080 " ^| findstr LISTENING') do taskkill /F /PID %%P
echo or close this window.
echo.

start "" /b dotnet run --project backend\Mira.Api\Mira.Api.csproj --no-launch-profile

pushd frontend
call npm run dev
set "FRONTEND_EXIT=%ERRORLEVEL%"
popd

echo.
echo Frontend stopped. Stopping the API if it is still listening on port 5080...
set "KILLED="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /C:":5080 " ^| findstr LISTENING') do (
  echo Stopping PID %%P
  taskkill /F /PID %%P
  set "KILLED=1"
)
if not defined KILLED echo No process was listening on port 5080.
exit /b %FRONTEND_EXIT%
