# Foundry IQ Multimodal Search Lab

This lab provisions Azure infrastructure with Terraform, generates fake enterprise files, uploads them to Blob Storage, builds an Azure AI Search enrichment pipeline, and queries the index from a .NET console app that uses Microsoft Agent Framework.

The search enrichment path is intentionally image-focused:

- Blob Storage contains `.pdf`, `.docx`, `.pptx`, `.png`, and `.jpg` sample enterprise files.
- The indexer enables `imageAction = generateNormalizedImages` so standalone and embedded images appear at `/document/normalized_images/*`.
- OCR extracts text from images with `#Microsoft.Skills.Vision.OcrSkill`.
- Image verbalization uses the GenAI Prompt skill `#Microsoft.Skills.Custom.ChatCompletionSkill` against the Azure OpenAI chat deployment, not Azure AI Vision image analysis.
- A Text Merge skill folds OCR and GenAI image descriptions into the searchable content.
- An Azure OpenAI embedding skill creates vectors for hybrid search.
- The .NET query command uses Microsoft Agent Framework with an Azure AI Search function tool, hybrid vector + keyword search, and semantic reranking.

## Prerequisites

- Azure CLI logged into tenant `5dc82be3-90ab-4f72-a0f2-b2557ba694e3` and subscription `ME-MngEnvMCAP861042-yaelghal-2`.
- Terraform installed.
- .NET 8 SDK installed. This machine now has SDK `8.0.422` under `C:\Program Files\dotnet\sdk`.
- Azure OpenAI quota in the selected region for `gpt-4o` and `text-embedding-3-small`.

Terraform is installed on this machine outside PATH. Use `scripts\terraform.cmd` or the VS Code Terraform tasks; the wrapper locates the WinGet-installed `terraform.exe` automatically.

## Provision Infra

```powershell
cd infra
..\scripts\terraform.cmd init
..\scripts\terraform.cmd apply -var "subscription_id=292e62e6-54a8-4c6a-b996-0c83f8cc29d0"
..\scripts\terraform.cmd output -json > ..\terraform.outputs.json
```

The default resource group name is `rg-foundry-iq-multimodal`. `location` controls Storage and Azure OpenAI and defaults to `eastus2`; `search_location` controls Azure AI Search plus the AI Services resource used by OCR and defaults to `eastus` because `eastus2` returned `InsufficientResourcesAvailable` for Search in this environment. Azure AI Search requires the attached OCR AI Services resource to be in the same region as the Search service. The lab uses managed identity for Storage, AI Services OCR billing, Azure OpenAI enrichment, and local Agent Framework calls, which keeps it compatible with tenants where key-based auth is disabled.

## Run The Lab

```powershell
.\scripts\run-lab.cmd
```

The script reads `terraform.outputs.json`, generates the data, uploads it, creates the Search index/skillset/data source/indexer, runs the indexer, validates OCR and image verbalization fields, then runs an Agent Framework query.

Current validation status in this environment:

- `.png`: indexed with OCR text and GenAI image description.
- `.jpg`: indexed with OCR text and GenAI image description.
- `.pdf`: indexed with document text plus OCR/GenAI output from the embedded image.
- `.docx`: indexed with document text plus OCR/GenAI output from the embedded image.
- `.pptx`: uploads and is counted by the indexer, and the generated file passes local OpenXML validation, but Azure AI Search document extraction returns empty content for this synthetic deck. The indexer reports non-fatal warnings for the missing merged text/vector on `platform-org-chart.pptx`.

Useful direct commands:

```powershell
dotnet run --project src\FoundryIqMultimodal -- generate-data
dotnet run --project src\FoundryIqMultimodal -- validate-sample-openxml
dotnet run --project src\FoundryIqMultimodal -- upload
dotnet run --project src\FoundryIqMultimodal -- configure-search
dotnet run --project src\FoundryIqMultimodal -- run-indexer
dotnet run --project src\FoundryIqMultimodal -- validate
dotnet run --project src\FoundryIqMultimodal -- agent-query "Which project steering document mentions supplier risk and what image evidence supports it?"
```

## What To Check If PNG Images Do Not Work

1. Confirm the indexer JSON contains `"imageAction": "generateNormalizedImages"`.
2. Confirm the skillset context for OCR and GenAI is `/document/normalized_images/*`.
3. Confirm the GenAI Prompt skill image input is `/document/normalized_images/*/data`.
4. Confirm output mappings include `/document/normalized_images/*/text` and `/document/normalized_images/*/imageDescription`.
5. Run `dotnet run --project src\FoundryIqMultimodal -- indexer-status` and inspect warnings about unsupported content types, missing model auth, or empty normalized images.
