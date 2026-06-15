@echo off
setlocal

set "TF=%ProgramFiles%\Terraform\terraform.exe"
if exist "%TF%" goto run

set "TF=%ProgramFiles%\HashiCorp\Terraform\terraform.exe"
if exist "%TF%" goto run

set "TF=%ProgramFiles(x86)%\Terraform\terraform.exe"
if exist "%TF%" goto run

set "TF=%LocalAppData%\Programs\Terraform\terraform.exe"
if exist "%TF%" goto run

for /d %%D in ("%LocalAppData%\Microsoft\WinGet\Packages\Hashicorp.Terraform_*") do (
  if exist "%%~fD\terraform.exe" (
    set "TF=%%~fD\terraform.exe"
    goto run
  )
)

echo terraform.exe was not found. Install Terraform or add it to PATH. 1>&2
exit /b 1

:run
"%TF%" %*
exit /b %ERRORLEVEL%
