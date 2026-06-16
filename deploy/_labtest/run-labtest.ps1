# LAB TEST runner: deploys the discus-style chunk pattern to srch-fdiq-8m8m9e and proves it indexes image content with vectors.
$ErrorActionPreference = "Stop"
$root = "c:\Users\yaelghal\Downloads\localDev\foundry-iq-multimodal"
$here = Join-Path $root "deploy\_labtest"
$ep   = "https://srch-fdiq-8m8m9e.search.windows.net"
$api  = "2026-04-01"

$o = Get-Content (Join-Path $root "terraform.outputs.json") -Raw | ConvertFrom-Json
$searchKey  = $o.search_admin_key.value
$openaiKey  = $o.openai_key.value
$aiKey      = $o.ai_services_key.value
$hdr = @{ "api-key" = $searchKey; "Content-Type" = "application/json" }

function Put-Obj($kind, $name, $file) {
  $body = Get-Content (Join-Path $here $file) -Raw
  $body = $body.Replace("__OPENAI_KEY__", $openaiKey).Replace("__AISERVICES_KEY__", $aiKey)
  try {
    Invoke-RestMethod -Method Put -Uri "$ep/$kind/$name`?api-version=$api" -Headers $hdr -Body $body | Out-Null
    Write-Host "PUT $kind/$name OK" -ForegroundColor Green
  } catch {
    Write-Host "PUT $kind/$name FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message -ForegroundColor DarkYellow }
    throw
  }
}

Put-Obj "indexes"    "discus-test-index"    "discus-test-index.json"
Put-Obj "skillsets"  "discus-test-skillset" "discus-test-skillset.json"
Put-Obj "indexers"   "discus-test-indexer"  "discus-test-indexer.json"

Write-Host "Running indexer..." -ForegroundColor Cyan
Invoke-RestMethod -Method Post -Uri "$ep/indexers/discus-test-indexer/run?api-version=$api" -Headers $hdr | Out-Null

for ($i = 0; $i -lt 40; $i++) {
  Start-Sleep -Seconds 15
  $s = Invoke-RestMethod "$ep/indexers/discus-test-indexer/status?api-version=$api" -Headers $hdr
  $st = $s.lastResult.status
  Write-Host ("  [{0}] status={1} processed={2} failed={3}" -f $i, $st, $s.lastResult.itemsProcessed, $s.lastResult.itemsFailed)
  if ($st -in @("success","transientFailure","reset") -or ($st -eq $null)) { }
  if ($st -ne "inProgress" -and $i -gt 0) {
    if ($s.lastResult.errors) { Write-Host "ERRORS:" -ForegroundColor Red; $s.lastResult.errors | ConvertTo-Json -Depth 6 }
    if ($s.lastResult.warnings) { Write-Host "WARNINGS:" -ForegroundColor Yellow; $s.lastResult.warnings | ConvertTo-Json -Depth 6 }
    break
  }
}

$cnt = Invoke-RestMethod "$ep/indexes/discus-test-index/docs/`$count?api-version=$api" -Headers $hdr
Write-Host "`nINDEX DOC COUNT = $cnt" -ForegroundColor Cyan

$q = Invoke-RestMethod "$ep/indexes/discus-test-index/docs/search?api-version=$api" -Headers $hdr -Method Post -Body (@{
  search = "*"; top = 2; select = "id,text_parent_id,metadata_storage_name,content"
} | ConvertTo-Json)
foreach ($d in $q.value) {
  $snippet = if ($d.content.Length -gt 280) { $d.content.Substring(0,280) } else { $d.content }
  Write-Host "`n--- chunk $($d.id) (parent=$($d.text_parent_id), file=$($d.metadata_storage_name)) ---" -ForegroundColor DarkCyan
  Write-Host $snippet
}

$vq = Invoke-RestMethod "$ep/indexes/discus-test-index/docs/search?api-version=$api" -Headers $hdr -Method Post -Body (@{
  search = "*"; top = 1; select = "id"; vectorQueries = @() } | ConvertTo-Json)
$one = Invoke-RestMethod "$ep/indexes/discus-test-index/docs/search?api-version=$api" -Headers $hdr -Method Post -Body (@{ search="*"; top=1; select="id,text_vector" } | ConvertTo-Json)
$vlen = ($one.value[0].text_vector).Count
Write-Host "`nVECTOR LENGTH on first chunk = $vlen" -ForegroundColor Cyan
