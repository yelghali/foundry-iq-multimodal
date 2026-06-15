@echo off
setlocal enabledelayedexpansion

set "OUTPUTS=%~dp0..\terraform.outputs.json"
set "QUERY=%~1"
if "%QUERY%"=="" set "QUERY=Which internal policy mentions badge access exceptions and what image evidence supports it?"

if not exist "%OUTPUTS%" (
  echo Missing terraform.outputs.json. Run: scripts\terraform.cmd -chdir=infra output -json ^> terraform.outputs.json 1>&2
  exit /b 1
)

set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
  echo dotnet.exe was not found at %DOTNET%. Install the .NET 8 SDK. 1>&2
  exit /b 1
)

set "PROJECT=%~dp0..\src\FoundryIqMultimodal"

for /f "delims=" %%V in ('""%DOTNET%" run --project "%PROJECT%" -- print-env-from-terraform "%OUTPUTS%""') do set "%%V"
if errorlevel 1 exit /b %ERRORLEVEL%

"%DOTNET%" run --project "%PROJECT%" -- generate-data || exit /b %ERRORLEVEL%
"%DOTNET%" run --project "%PROJECT%" -- upload || exit /b %ERRORLEVEL%
"%DOTNET%" run --project "%PROJECT%" -- configure-search || exit /b %ERRORLEVEL%
"%DOTNET%" run --project "%PROJECT%" -- run-indexer || exit /b %ERRORLEVEL%
"%DOTNET%" run --project "%PROJECT%" -- validate || exit /b %ERRORLEVEL%
"%DOTNET%" run --project "%PROJECT%" -- agent-query "%QUERY%"
