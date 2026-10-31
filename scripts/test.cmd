@echo off
setlocal
cd /d "%~dp0.."

dotnet build backend\Mira.sln -m:1
if errorlevel 1 exit /b 1

dotnet test backend\Mira.sln --no-build -m:1
if errorlevel 1 exit /b 1

pushd frontend
if not exist "node_modules" (
  call npm ci
  if errorlevel 1 exit /b 1
)

call npm test
if errorlevel 1 exit /b 1

call npm run check
if errorlevel 1 exit /b 1

call npm run build
if errorlevel 1 exit /b 1
popd

echo All checks passed.
exit /b 0
