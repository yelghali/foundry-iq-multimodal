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

## Indexer And Skillset Code

Implementation path: `src/FoundryIqMultimodal/Program.cs`

The indexer is configured in `SearchConfigurator.BuildIndexer`. It enables normalized image generation for standalone images and embedded images inside supported documents, then maps OCR, GenAI image descriptions, merged text, and vectors into the index.

```csharp
private static object BuildIndexer(LabConfig config) => new
{
	name = config.SearchIndexerName,
	dataSourceName = config.SearchDataSourceName,
	targetIndexName = config.SearchIndexName,
	skillsetName = config.SearchSkillsetName,
	parameters = new
	{
		configuration = new
		{
			dataToExtract = "contentAndMetadata",
			parsingMode = "default",
			imageAction = "generateNormalizedImages",
			indexedFileNameExtensions = ".pdf,.docx,.pptx,.png,.jpg,.jpeg"
		}
	},
	fieldMappings = new[]
	{
		new { sourceFieldName = "metadata_storage_path", targetFieldName = "id", mappingFunction = new { name = "base64Encode" } }
	},
	outputFieldMappings = new object[]
	{
		new { sourceFieldName = "/document/merged_all", targetFieldName = "merged_content" },
		new { sourceFieldName = "/document/normalized_images/*/text", targetFieldName = "ocrText" },
		new { sourceFieldName = "/document/normalized_images/*/layoutText", targetFieldName = "ocrLayoutText" },
		new { sourceFieldName = "/document/normalized_images/*/imageDescription", targetFieldName = "imageDescription" },
		new { sourceFieldName = "/document/contentVector", targetFieldName = "contentVector" }
	}
};
```

The skillset is configured in `SearchConfigurator.BuildSkillset`. The skills used are `#Microsoft.Skills.Vision.OcrSkill`, `#Microsoft.Skills.Custom.ChatCompletionSkill`, two `#Microsoft.Skills.Text.MergeSkill` steps, and `#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill`. The excerpt below shows the OCR, GenAI Prompt, and embedding parts; the full method in `Program.cs` includes the merge steps that fold OCR and image descriptions into `/document/merged_all`.

```csharp
private static object BuildSkillset(LabConfig config) => new
{
	name = config.SearchSkillsetName,
	description = "OCR + GenAI image verbalization skillset for enterprise multimodal files.",
	cognitiveServices = Obj(
		("@odata.type", "#Microsoft.Azure.Search.AIServicesByIdentity"),
		("description", "Keyless AI Services billing resource for OCR"),
		("subdomainUrl", config.AiServicesEndpoint.ToString().TrimEnd('/')),
		("identity", null)),
	skills = new object[]
	{
		Obj(
			("@odata.type", "#Microsoft.Skills.Vision.OcrSkill"),
			("name", "ocr-images"),
			("context", "/document/normalized_images/*"),
			("inputs", new[] { new { name = "image", source = "/document/normalized_images/*" } }),
			("outputs", new[]
			{
				new { name = "text", targetName = "text" },
				new { name = "layoutText", targetName = "layoutText" }
			})),
		Obj(
			("@odata.type", "#Microsoft.Skills.Custom.ChatCompletionSkill"),
			("name", "verbalize-images-with-llm"),
			("context", "/document/normalized_images/*"),
			("uri", $"{config.OpenAiEndpoint.ToString().TrimEnd('/')}/openai/deployments/{config.ChatDeploymentName}/chat/completions?api-version=2024-10-21"),
			("inputs", new object[]
			{
				new { name = "image", source = "/document/normalized_images/*/data" },
				new { name = "imageDetail", source = "='high'" },
				new { name = "systemMessage", source = "='You verbalize enterprise document images for retrieval. Be factual and include visible text, chart labels, people names, owners, risks, policies, process steps, and org relationships. Do not invent facts.'" },
				new { name = "userMessage", source = "='Describe this image for an enterprise search index. Include any visible words and why the image matters.'" }
			}),
			("outputs", new[] { new { name = "response", targetName = "imageDescription" } })),
		Obj(
			("@odata.type", "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill"),
			("name", "embed-merged-content"),
			("context", "/document"),
			("resourceUri", config.OpenAiEndpoint.ToString().TrimEnd('/')),
			("deploymentId", config.EmbeddingDeploymentName),
			("modelName", "text-embedding-3-small"),
			("dimensions", 1536),
			("inputs", new[] { new { name = "text", source = "/document/merged_all" } }),
			("outputs", new[] { new { name = "embedding", targetName = "contentVector" } }))
	}
};
```

## Client Query Code

Implementation path: `src/FoundryIqMultimodal/Program.cs`

The client query is implemented by `AgentQueryDemo.RunAsync`, `SearchValidator.HybridSemanticSearchAsync`, and `Embeddings.GetEmbeddingAsync`. The Microsoft Agent Framework agent exposes Azure AI Search as a tool, and the tool sends a single hybrid request with keyword text, vector search, and semantic reranking.

```csharp
static class AgentQueryDemo
{
	public static async Task RunAsync(LabConfig config, string question)
	{
		var searchTool = AIFunctionFactory.Create(async ([Description("The enterprise search question.")] string query) =>
			await SearchValidator.HybridSemanticSearchAsync(config, query),
			name: "search_enterprise_multimodal_index",
			description: "Runs Azure AI Search hybrid vector and keyword search with semantic reranking over OCR and GenAI image descriptions.");

		var azureOpenAiClient = new AzureOpenAIClient(config.OpenAiEndpoint, new DefaultAzureCredential());
		ChatClient chatClient = azureOpenAiClient.GetChatClient(config.ChatDeploymentName);
		AIAgent agent = chatClient.AsAIAgent(
			instructions: "You answer from the enterprise multimodal search index only. Call the search tool first. Cite document names and separate OCR evidence from GenAI image-description evidence when present.",
			name: "EnterpriseSearchAgent",
			tools: [searchTool]);

		var answer = await agent.RunAsync(question);
		Console.WriteLine(answer);
	}
}
```

The hybrid search request combines `search = question` with `vectorQueries`, then asks Azure AI Search to use the semantic configuration for reranking, captions, and extractive answers.

```csharp
public static async Task<string> HybridSemanticSearchAsync(LabConfig config, string question)
{
	var vector = await Embeddings.GetEmbeddingAsync(config, question);
	using var client = SearchHttpClient(config);
	var body = new
	{
		search = question,
		vectorQueries = new object[]
		{
			new { kind = "vector", vector, fields = "contentVector", k = 5 }
		},
		queryType = "semantic",
		semanticConfiguration = "semantic-config",
		captions = "extractive|highlight-true",
		answers = "extractive|count-3",
		select = "metadata_storage_name,merged_content,ocrText,imageDescription",
		top = 5
	};

	return await PostSearchAsync(client, config, body);
}
```

The query vector is created client-side with Azure OpenAI embeddings and Entra ID auth.

```csharp
public static async Task<float[]> GetEmbeddingAsync(LabConfig config, string text)
{
	using var client = new HttpClient { BaseAddress = config.OpenAiEndpoint };
	var token = await new DefaultAzureCredential().GetTokenAsync(
		new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

	var request = new { input = text, dimensions = 1536 };
	var uri = $"/openai/deployments/{config.EmbeddingDeploymentName}/embeddings?api-version=2024-10-21";
	var response = await client.PostAsync(uri, new StringContent(JsonSerializer.Serialize(request, Json.Options), Encoding.UTF8, "application/json"));
	var json = await response.Content.ReadAsStringAsync();

	using var doc = JsonDocument.Parse(json);
	return doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
}
```

## What To Check If PNG Images Do Not Work

1. Confirm the indexer JSON contains `"imageAction": "generateNormalizedImages"`.
2. Confirm the skillset context for OCR and GenAI is `/document/normalized_images/*`.
3. Confirm the GenAI Prompt skill image input is `/document/normalized_images/*/data`.
4. Confirm output mappings include `/document/normalized_images/*/text` and `/document/normalized_images/*/imageDescription`.
5. Run `dotnet run --project src\FoundryIqMultimodal -- indexer-status` and inspect warnings about unsupported content types, missing model auth, or empty normalized images.
