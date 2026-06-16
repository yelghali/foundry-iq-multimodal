# Updates skl-discus + idxr-discus on the live Azure AI Search service, then resets and reruns the indexer.
# Keyless: uses your Azure AD token (tenant disables key-based auth).
#
# Prerequisites:
#   1. az login  (account must hold the "Search Service Contributor" role on the search service)
#   2. The search service managed identity must have "Cognitive Services OpenAI User" on aif-companion-nprd
#   3. The data source "ds-discus" must already exist.
#
# Usage:
#   ./deploy.ps1
#   ./deploy.ps1 -ServiceName srch-companion-shared-nprd -ApiVersion 2026-04-01

[CmdletBinding()]
param(
    [string]$ServiceName = "srch-companion-shared-nprd",
    [string]$ApiVersion  = "2026-04-01",
    [string]$AzCli       = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
)

$ErrorActionPreference = "Stop"
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$endpoint = "https://$ServiceName.search.windows.net"

Write-Host "Acquiring Azure AD token for the Azure AI Search data plane..." -ForegroundColor Cyan
$token = (& $AzCli account get-access-token --resource "https://search.azure.com" --query accessToken -o tsv)
if (-not $token) { throw "Failed to acquire token. Run 'az login' first." }

$headers = @{
    Authorization  = "Bearer $token"
    "Content-Type" = "application/json"
}

function Put-Resource {
    param([string]$Kind, [string]$Name, [string]$File)
    $uri  = "$endpoint/$Kind/$Name`?api-version=$ApiVersion"
    $body = Get-Content -LiteralPath (Join-Path $here $File) -Raw
    Write-Host "PUT $Kind/$Name ..." -ForegroundColor Cyan
    Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -Body $body | Out-Null
    Write-Host "  OK" -ForegroundColor Green
}

# 1. Update the index (idempotent; matches the live idx-discus schema)
Put-Resource -Kind "indexes" -Name "idx-discus" -File "idx-discus.index.json"

# 2. Update the skillset (adds split-skill, fixes embedding input -> /document/pages/*)
Put-Resource -Kind "skillsets" -Name "skl-discus" -File "skl-discus.skillset.json"

# 3. Update the indexer (outputFieldMappings cleared; chunk content carries OCR + image text)
Put-Resource -Kind "indexers" -Name "idxr-discus" -File "idxr-discus.indexer.json"

# 3. Reset + run so existing documents are re-enriched from scratch
Write-Host "Resetting indexer idxr-discus ..." -ForegroundColor Cyan
Invoke-RestMethod -Method Post -Uri "$endpoint/indexers/idxr-discus/reset?api-version=$ApiVersion" -Headers $headers | Out-Null
Write-Host "Running indexer idxr-discus ..." -ForegroundColor Cyan
Invoke-RestMethod -Method Post -Uri "$endpoint/indexers/idxr-discus/run?api-version=$ApiVersion" -Headers $headers | Out-Null

Write-Host "`nDone. Check status with:" -ForegroundColor Green
Write-Host "  Invoke-RestMethod -Headers @{Authorization='Bearer <token>'} `"$endpoint/indexers/idxr-discus/status?api-version=$ApiVersion`"" -ForegroundColor DarkGray
