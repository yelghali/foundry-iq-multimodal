param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TerraformArgs
)

$ErrorActionPreference = "Stop"

$candidates = @(
    "$env:ProgramFiles\Terraform\terraform.exe",
    "$env:ProgramFiles\HashiCorp\Terraform\terraform.exe",
    "${env:ProgramFiles(x86)}\Terraform\terraform.exe",
    "$env:LocalAppData\Programs\Terraform\terraform.exe"
)

$terraform = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $terraform) {
    $terraform = Get-ChildItem -Path "$env:LocalAppData\Microsoft\WinGet\Packages" -Filter terraform.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $terraform) {
    throw "terraform.exe was not found. Install Terraform or add it to PATH."
}

& $terraform @TerraformArgs
exit $LASTEXITCODE
