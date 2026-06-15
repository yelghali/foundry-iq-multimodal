param(
    [string]$OutputsPath = "$PSScriptRoot\..\terraform.outputs.json",
    [string]$Query = "Which internal policy mentions badge access exceptions and what image evidence supports it?"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $OutputsPath)) {
    throw "Missing Terraform outputs file. Run: terraform -chdir=infra output -json > terraform.outputs.json"
}

$outputs = Get-Content $OutputsPath -Raw | ConvertFrom-Json

function Set-LabEnv($name, $outputName) {
    $value = $outputs.$outputName.value
    if (-not $value) { throw "Terraform output '$outputName' is missing." }
    [Environment]::SetEnvironmentVariable($name, $value, "Process")
}

Set-LabEnv "AZURE_STORAGE_BLOB_ENDPOINT" "storage_blob_endpoint"
Set-LabEnv "AZURE_STORAGE_ACCOUNT_ID" "storage_account_id"
Set-LabEnv "BLOB_CONTAINER_NAME" "storage_container_name"
Set-LabEnv "AZURE_SEARCH_ENDPOINT" "search_endpoint"
Set-LabEnv "AZURE_SEARCH_ADMIN_KEY" "search_admin_key"
Set-LabEnv "AZURE_SEARCH_INDEX_NAME" "search_index_name"
Set-LabEnv "AZURE_SEARCH_SKILLSET_NAME" "search_skillset_name"
Set-LabEnv "AZURE_SEARCH_INDEXER_NAME" "search_indexer_name"
Set-LabEnv "AZURE_SEARCH_DATASOURCE_NAME" "search_datasource_name"
Set-LabEnv "AZURE_OPENAI_ENDPOINT" "openai_endpoint"
Set-LabEnv "AZURE_OPENAI_CHAT_DEPLOYMENT" "chat_deployment_name"
Set-LabEnv "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" "embedding_deployment_name"
Set-LabEnv "AZURE_AI_SERVICES_ENDPOINT" "ai_services_endpoint"

$project = "$PSScriptRoot\..\src\FoundryIqMultimodal"

dotnet run --project $project -- generate-data
dotnet run --project $project -- upload
dotnet run --project $project -- configure-search
dotnet run --project $project -- run-indexer
dotnet run --project $project -- validate
dotnet run --project $project -- agent-query $Query
