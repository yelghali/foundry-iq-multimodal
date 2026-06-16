# discus — multimodal ingestion fix (OCR + image verbalization + merge + chunk + vectors)

These files patch the **live** Azure AI Search objects on `srch-companion-shared-nprd`.
They are not part of the .NET lab — the lab (`src/FoundryIqMultimodal`) uses a different,
already-working parent-document pattern.

## Files

| File | Azure object | Change |
| --- | --- | --- |
| `idx-discus.index.json` | index `idx-discus` | unchanged (full schema, for reference / idempotent deploy) |
| `skl-discus.skillset.json` | skillset `skl-discus` | added `split-skill`; embedding input fixed to `/document/pages/*` |
| `idxr-discus.indexer.json` | indexer `idxr-discus` | `outputFieldMappings` cleared to `[]` |
| `deploy.ps1` | — | keyless (AAD) create-or-update of all three, then reset + run |

## What was broken

Your `skl-discus` skillset declared an embedding skill and index projections that read
`/document/pages/*`, but **no skill produced `pages`**. With
`projectionMode: skipIndexingParentDocuments`, that means:

- 0 chunks generated → **0 documents indexed** (parents are skipped, children never existed).
- `outputFieldMappings` on the indexer wrote to the skipped parent doc, so
  `merged_content` / `ocrText` / `ocrLayoutText` / `imageDescription` always stayed empty,
  and `/document/normalized_images/*/layoutText` doesn't exist (the OCR skill only emits `text`).

## The fix (two changes)

1. **Added a `SplitSkill`** (`split-skill`) that chunks `/document/merged_all` → `/document/pages/*`.
   This is the missing piece that feeds both the embedding skill and the projections.
2. **Embedding input** changed from `/document/merged_all` (document level) to
   `/document/pages/*` (chunk level), matching its `context`.
3. Indexer `outputFieldMappings` cleared (`[]`) — the OCR text and image descriptions are already
   merged into `merged_all`, chunked, and projected into each chunk's `content` field.

The enrichment chain now is:

`OCR` + `GenAI image description` → `merge-skill` (merged_ocr_text) → `merge-image-descriptions`
(merged_all) → **`split-skill` (pages)** → `embedding` (contentVector per chunk) → index projections.

> Note: `SplitSkill` must not include a `unit` property — it isn't valid in REST `2026-04-01`
> (the default unit is characters).

## Validated end-to-end on the lab service

This exact pattern was deployed and run against the reachable lab service
`srch-fdiq-8m8m9e` (test objects `discus-test-index` / `discus-test-skillset` /
`discus-test-indexer`, using the lab's own `gpt-4o` + `text-embedding-3-small`).
Result against the 5 sample documents:

| Source file | Chunked? | Content captured |
| --- | --- | --- |
| `internal-policy-access-control.pdf` | yes | doc text + OCR of embedded process image |
| `internal-policy-badge-access.png`  | yes | GenAI description of "BADGE EXCEPTION FLOW" |
| `project-steering-risk.jpg`         | yes | GenAI description of the risk slide |
| `project-steering-pack.docx`        | yes | doc text |
| `platform-org-chart.pptx`           | **no** | empty — see caveat below |

Each chunk had a populated 1536-dim `text_vector`, and chunk `content` contained the
`[GenAI image description] ...` text. This proves OCR + image verbalization + merged
content + per-chunk vectorization all work in the chunk pattern.

### Caveat: image-only Office files (.pptx/.docx)

`platform-org-chart.pptx` produced no OCR text and no image description **in both the chunk
pattern and the existing parent-document pattern** — Azure AI Search's `generateNormalizedImages`
does not crack images **embedded inside Office documents** (it does for PDFs and standalone image
files). In the parent pattern that file is still indexed as a blank document; in the chunk pattern
it is correctly dropped (empty `merged_all` → 0 chunks).

If you need org charts / diagrams that live inside `.pptx`/`.docx` to be verbalized, **export them
to PDF or images before ingestion**. For native PDFs and image files, the pipeline works as-is.

### Removing the lab test objects

The validation objects are left on `srch-fdiq-8m8m9e` (no schedule, so they won't re-run). To delete:

```powershell
$ep="https://srch-fdiq-8m8m9e.search.windows.net"; $api="2026-04-01"
$o=Get-Content ..\terraform.outputs.json -Raw | ConvertFrom-Json
$h=@{ "api-key"=$o.search_admin_key.value }
"indexers/discus-test-indexer","skillsets/discus-test-skillset","indexes/discus-test-index" |
  ForEach-Object { Invoke-RestMethod -Method Delete -Uri "$ep/$_`?api-version=$api" -Headers $h }
```

## Deploy

```powershell
az login            # account needs "Search Service Contributor" on the search service
./deploy.ps1
```

The script PUTs the skillset + indexer, then resets and reruns the indexer.

## Notes / prerequisites

- The search service's **managed identity** needs **Cognitive Services OpenAI User** on
  `aif-companion-nprd` (used by both the `ChatCompletionSkill` and the embedding skill).
- The data source `ds-discus` must already exist (not recreated here).
- The index `idx-discus` needs **no change** — it already has `content`, `text_vector` (3072),
  and `text_parent_id`. The extra fields you added (`merged_content`, `ocrText`, `ocrLayoutText`,
  `imageDescription`) will stay empty in the chunk pattern; that's expected and harmless.
- After running, check `idxr-discus/status` for warnings and confirm the index document count
  is now > 0.
