using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImgColor = SixLabors.ImageSharp.Color;

QuestPDF.Settings.License = LicenseType.Community;

var command = args.FirstOrDefault() ?? "help";

if (command == "print-env-from-terraform")
{
    TerraformEnvPrinter.Print(args.Skip(1).FirstOrDefault() ?? "terraform.outputs.json");
    return;
}

if (command == "generate-data")
{
    var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sample-data"));
    EnterpriseSampleData.Generate(Environment.GetEnvironmentVariable("SAMPLE_DATA_PATH") ?? basePath);
    return;
}

if (command == "validate-sample-openxml")
{
    var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sample-data"));
    EnterpriseSampleData.ValidateOpenXml(Environment.GetEnvironmentVariable("SAMPLE_DATA_PATH") ?? basePath);
    return;
}

var config = LabConfig.FromEnvironment();

try
{
    switch (command)
    {
        case "upload":
            await BlobUploader.UploadAsync(config);
            break;
        case "configure-search":
            await SearchConfigurator.ConfigureAsync(config);
            break;
        case "run-indexer":
            await SearchConfigurator.RunIndexerAsync(config);
            await SearchConfigurator.WaitForIndexerAsync(config);
            break;
        case "indexer-status":
            await SearchConfigurator.PrintIndexerStatusAsync(config);
            break;
        case "validate":
            await SearchValidator.ValidateAsync(config);
            break;
        case "agent-query":
            var query = string.Join(' ', args.Skip(1));
            if (string.IsNullOrWhiteSpace(query))
            {
                query = "Which project steering document mentions supplier risk and what image evidence supports it?";
            }
            await AgentQueryDemo.RunAsync(config, query);
            break;
        default:
            Console.WriteLine("Commands: generate-data, upload, configure-search, run-indexer, indexer-status, validate, agent-query <question>");
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

sealed record LabConfig(
    Uri StorageBlobEndpoint,
    string StorageAccountId,
    string BlobContainerName,
    Uri SearchEndpoint,
    string SearchAdminKey,
    string SearchIndexName,
    string SearchSkillsetName,
    string SearchIndexerName,
    string SearchDataSourceName,
    Uri OpenAiEndpoint,
    string ChatDeploymentName,
    string EmbeddingDeploymentName,
    Uri AiServicesEndpoint,
    string SampleDataPath)
{
    public static LabConfig FromEnvironment()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sample-data");
        return new LabConfig(
            new Uri(Required("AZURE_STORAGE_BLOB_ENDPOINT")),
            Required("AZURE_STORAGE_ACCOUNT_ID"),
            Required("BLOB_CONTAINER_NAME"),
            new Uri(Required("AZURE_SEARCH_ENDPOINT")),
            Required("AZURE_SEARCH_ADMIN_KEY"),
            Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX_NAME") ?? "enterprise-multimodal",
            Environment.GetEnvironmentVariable("AZURE_SEARCH_SKILLSET_NAME") ?? "enterprise-multimodal-skillset",
            Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEXER_NAME") ?? "enterprise-multimodal-indexer",
            Environment.GetEnvironmentVariable("AZURE_SEARCH_DATASOURCE_NAME") ?? "enterprise-multimodal-blob",
            new Uri(Required("AZURE_OPENAI_ENDPOINT")),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT") ?? "gpt-4o",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-3-small",
            new Uri(Required("AZURE_AI_SERVICES_ENDPOINT")),
            Path.GetFullPath(Environment.GetEnvironmentVariable("SAMPLE_DATA_PATH") ?? basePath));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set environment variable {name}.");
}

static class EnterpriseSampleData
{
    public static void Generate(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }

        Directory.CreateDirectory(outputPath);
        var images = Path.Combine(outputPath, "source-images");
        Directory.CreateDirectory(images);

        var badgePng = Path.Combine(images, "badge-exception-flow.png");
        var steeringJpg = Path.Combine(images, "supplier-risk-steering.jpg");
        var orgPng = Path.Combine(images, "org-chart-platform-team.png");

        CreateImage(badgePng, "BADGE EXCEPTION FLOW", ["Visitor escort required", "Approver: Site Security", "Policy ID: INT-SEC-014"], ImgColor.ParseHex("1d4ed8"));
        CreateImage(steeringJpg, "PROJECT STEERING RISK", ["Supplier delay: high", "Mitigation: dual source", "Owner: Maya Chen"], ImgColor.ParseHex("b45309"));
        CreateImage(orgPng, "PLATFORM ORG CHART", ["VP Ops: Priya Raman", "AI Search Lead: Noah Kim", "Data Steward: Lina Ortiz"], ImgColor.ParseHex("047857"));

        File.Copy(badgePng, Path.Combine(outputPath, "internal-policy-badge-access.png"));
        File.Copy(steeringJpg, Path.Combine(outputPath, "project-steering-risk.jpg"));

        CreatePolicyPdf(Path.Combine(outputPath, "internal-policy-access-control.pdf"), badgePng);
        CreateWordDocument(Path.Combine(outputPath, "project-steering-pack.docx"), steeringJpg);
        CreatePowerPoint(Path.Combine(outputPath, "platform-org-chart.pptx"), orgPng);

        Console.WriteLine($"Generated sample enterprise data in {outputPath}");
    }

    public static void ValidateOpenXml(string sampleDataPath)
    {
        var pptxPath = Path.Combine(sampleDataPath, "platform-org-chart.pptx");
        using var document = PresentationDocument.Open(pptxPath, false);
        var errors = new OpenXmlValidator().Validate(document).Take(20).ToList();
        if (errors.Count == 0)
        {
            Console.WriteLine("platform-org-chart.pptx passed OpenXML validation.");
            return;
        }

        foreach (var error in errors)
        {
            Console.WriteLine($"{error.Path?.XPath}: {error.Description}");
        }

        throw new InvalidOperationException($"platform-org-chart.pptx has {errors.Count} OpenXML validation issue(s).");
    }

    private static void CreateImage(string path, string title, string[] lines, ImgColor accent)
    {
        using var image = new Image<Rgba32>(1280, 720, ImgColor.White);
        var titleFont = SystemFonts.CreateFont("Arial", 58, FontStyle.Bold);
        var bodyFont = SystemFonts.CreateFont("Arial", 38, FontStyle.Regular);

        image.Mutate(ctx =>
        {
            ctx.Fill(ImgColor.ParseHex("f8fafc"));
            ctx.Fill(accent, new RectangleF(0, 0, 1280, 110));
            ctx.DrawText(title, titleFont, ImgColor.White, new PointF(48, 24));
            ctx.Draw(ImgColor.ParseHex("334155"), 4, new RectangleF(55, 175, 1170, 455));

            for (var i = 0; i < lines.Length; i++)
            {
                var y = 225 + i * 120;
                ctx.Fill(accent, new SixLabors.ImageSharp.Drawing.EllipsePolygon(92, y + 18, 16));
                ctx.DrawText(lines[i], bodyFont, ImgColor.ParseHex("0f172a"), new PointF(135, y));
            }
        });

        image.Save(path);
    }

    private static void CreatePolicyPdf(string path, string imagePath)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(48);
                page.Size(PageSizes.A4);
                page.Content().Column(column =>
                {
                    column.Item().Text("Internal Policy: Access Control Exceptions").FontSize(22).Bold();
                    column.Item().Text("Policy INT-SEC-014 governs temporary visitor badge exceptions, escort requirements, and site security approval routing.");
                    column.Item().PaddingVertical(16).Image(imagePath).FitWidth();
                    column.Item().Text("The embedded process image must be searchable through OCR and GenAI image verbalization.");
                });
            });
        }).GeneratePdf(path);
    }

    private static void CreateWordDocument(string path, string imagePath)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var body = main.Document.Body!;
        body.Append(Paragraph("Project Steering Pack: Foundry IQ Rollout", true));
        body.Append(Paragraph("The steering committee tracks supplier risk, model governance, regional data residency, and release readiness for the enterprise knowledge rollout.", false));

        var imagePart = main.AddImagePart(ImagePartType.Jpeg);
        using (var stream = File.OpenRead(imagePath))
        {
            imagePart.FeedData(stream);
        }

        body.Append(CreateWordImageParagraph(main.GetIdOfPart(imagePart), "Supplier risk steering image"));
        body.Append(Paragraph("Expected validation: OCR extracts labels from the embedded image and GenAI verbalization describes the supplier delay risk visual.", false));
        main.Document.Save();
    }

    private static W.Paragraph Paragraph(string text, bool heading)
    {
        var run = new W.Run(new W.Text(text));
        if (heading)
        {
            run.RunProperties = new W.RunProperties(new W.Bold(), new W.FontSize { Val = "32" });
        }

        return new W.Paragraph(run);
    }

    private static W.Paragraph CreateWordImageParagraph(string relationshipId, string name)
    {
        var element = new A.Blip { Embed = relationshipId };
        var drawing = new W.Drawing(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 5486400, Cy = 3086100 },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = name },
                new A.Graphic(new A.GraphicData(
                    new A.Pictures.Picture(
                        new A.Pictures.NonVisualPictureProperties(
                            new A.Pictures.NonVisualDrawingProperties { Id = 0U, Name = name },
                            new A.Pictures.NonVisualPictureDrawingProperties()),
                        new A.Pictures.BlipFill(element, new A.Stretch(new A.FillRectangle())),
                        new A.Pictures.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 5486400, Cy = 3086100 }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));

        return new W.Paragraph(new W.Run(drawing));
    }

    private static void CreatePowerPoint(string path, string imagePath)
    {
        using var presentation = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = presentation.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(new P.SlideMasterIdList(), new P.SlideIdList(), new P.SlideSize { Cx = 9144000, Cy = 6858000 }, new P.NotesSize { Cx = 6858000, Cy = 9144000 });

        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        slideMasterPart.SlideMaster = new P.SlideMaster(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1, Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2, Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4, Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6, Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
            new P.SlideLayoutIdList(),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        var themePart = slideMasterPart.AddNewPart<ThemePart>();
        WritePresentationTheme(themePart);

        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        slideLayoutPart.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMapOverride(new A.MasterColorMapping()));
        slideMasterPart.SlideMaster.SlideLayoutIdList!.Append(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart) });
        presentationPart.Presentation.SlideMasterIdList!.Append(new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(slideMasterPart) });
        slideLayoutPart.SlideLayout.Save();
        slideMasterPart.SlideMaster.Save();

        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(slideLayoutPart);
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(imagePath))
        {
            imagePart.FeedData(stream);
        }

        slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            CreateSlideTitle("Platform Operations Org Chart"),
            CreateSlidePicture(slidePart.GetIdOfPart(imagePart)))),
            new P.ColorMapOverride(new A.MasterColorMapping()));

        presentationPart.Presentation.SlideIdList!.Append(new P.SlideId { Id = 256U, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        slidePart.Slide.Save();
        presentationPart.Presentation.Save();
    }

        private static void WritePresentationTheme(ThemePart themePart)
        {
                const string themeXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Foundry IQ">
    <a:themeElements>
        <a:clrScheme name="Foundry IQ">
            <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
            <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
            <a:dk2><a:srgbClr val="1F2937"/></a:dk2>
            <a:lt2><a:srgbClr val="F8FAFC"/></a:lt2>
            <a:accent1><a:srgbClr val="2563EB"/></a:accent1>
            <a:accent2><a:srgbClr val="047857"/></a:accent2>
            <a:accent3><a:srgbClr val="B45309"/></a:accent3>
            <a:accent4><a:srgbClr val="7C3AED"/></a:accent4>
            <a:accent5><a:srgbClr val="0F766E"/></a:accent5>
            <a:accent6><a:srgbClr val="BE123C"/></a:accent6>
            <a:hlink><a:srgbClr val="2563EB"/></a:hlink>
            <a:folHlink><a:srgbClr val="7C3AED"/></a:folHlink>
        </a:clrScheme>
        <a:fontScheme name="Foundry IQ">
            <a:majorFont><a:latin typeface="Arial"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
            <a:minorFont><a:latin typeface="Arial"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
        </a:fontScheme>
        <a:fmtScheme name="Foundry IQ">
            <a:fillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"><a:tint val="50000"/><a:satMod val="300000"/></a:schemeClr></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"><a:tint val="80000"/><a:satMod val="200000"/></a:schemeClr></a:solidFill>
            </a:fillStyleLst>
            <a:lnStyleLst>
                <a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
                <a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
                <a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
            </a:lnStyleLst>
            <a:effectStyleLst>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
            </a:effectStyleLst>
            <a:bgFillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/><a:satMod val="170000"/></a:schemeClr></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"><a:tint val="85000"/><a:satMod val="150000"/></a:schemeClr></a:solidFill>
            </a:bgFillStyleLst>
        </a:fmtScheme>
    </a:themeElements>
    <a:objectDefaults/>
    <a:extraClrSchemeLst/>
</a:theme>
""";

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(themeXml));
                themePart.FeedData(stream);
        }

    private static P.Shape CreateSlideTitle(string text) =>
        new(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" }, new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }), new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 457200, Y = 274320 }, new A.Extents { Cx = 8229600, Cy = 685800 })),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.RunProperties { FontSize = 3200 }, new A.Text(text)))));

    private static P.Picture CreateSlidePicture(string relationshipId) =>
        new(
            new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = 3U, Name = "Org chart image" }, new P.NonVisualPictureDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 914400, Y = 1371600 }, new A.Extents { Cx = 7315200, Cy = 4114800 }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
}

static class TerraformEnvPrinter
{
    public static void Print(string outputsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(outputsPath));
        Emit(doc, "AZURE_STORAGE_BLOB_ENDPOINT", "storage_blob_endpoint");
        Emit(doc, "AZURE_STORAGE_ACCOUNT_ID", "storage_account_id");
        Emit(doc, "BLOB_CONTAINER_NAME", "storage_container_name");
        Emit(doc, "AZURE_SEARCH_ENDPOINT", "search_endpoint");
        Emit(doc, "AZURE_SEARCH_ADMIN_KEY", "search_admin_key");
        Emit(doc, "AZURE_SEARCH_INDEX_NAME", "search_index_name");
        Emit(doc, "AZURE_SEARCH_SKILLSET_NAME", "search_skillset_name");
        Emit(doc, "AZURE_SEARCH_INDEXER_NAME", "search_indexer_name");
        Emit(doc, "AZURE_SEARCH_DATASOURCE_NAME", "search_datasource_name");
        Emit(doc, "AZURE_OPENAI_ENDPOINT", "openai_endpoint");
        Emit(doc, "AZURE_OPENAI_CHAT_DEPLOYMENT", "chat_deployment_name");
        Emit(doc, "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "embedding_deployment_name");
        Emit(doc, "AZURE_AI_SERVICES_ENDPOINT", "ai_services_endpoint");
    }

    private static void Emit(JsonDocument doc, string envName, string outputName)
    {
        var value = doc.RootElement.GetProperty(outputName).GetProperty("value").GetString();
        Console.WriteLine($"{envName}={value}");
    }
}

static class BlobUploader
{
    public static async Task UploadAsync(LabConfig config)
    {
        var service = new BlobServiceClient(config.StorageBlobEndpoint, new DefaultAzureCredential());
        var container = service.GetBlobContainerClient(config.BlobContainerName);
        await container.CreateIfNotExistsAsync();

        foreach (var file in Directory.EnumerateFiles(config.SampleDataPath).Where(IsSupported))
        {
            var blob = container.GetBlobClient(Path.GetFileName(file));
            await blob.UploadAsync(file, overwrite: true);
            Console.WriteLine($"Uploaded {Path.GetFileName(file)}");
        }
    }

    private static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx" or ".pptx" or ".png" or ".jpg" or ".jpeg";
}

static class SearchConfigurator
{
    private const string ApiVersion = "2026-04-01";

    public static async Task ConfigureAsync(LabConfig config)
    {
        using var client = CreateClient(config);
        await PutAsync(client, config, $"/indexes/{config.SearchIndexName}", BuildIndex(config));
        await PutAsync(client, config, $"/skillsets/{config.SearchSkillsetName}", BuildSkillset(config));
        await PutAsync(client, config, $"/datasources/{config.SearchDataSourceName}", BuildDataSource(config));
        await PutAsync(client, config, $"/indexers/{config.SearchIndexerName}", BuildIndexer(config));
        Console.WriteLine("Configured index, skillset, data source, and indexer.");
    }

    public static async Task RunIndexerAsync(LabConfig config)
    {
        using var client = CreateClient(config);
        var response = await client.PostAsync(Uri(config, $"/indexers/{config.SearchIndexerName}/run"), null);
        response.EnsureSuccessStatusCode();
        Console.WriteLine("Indexer run requested.");
    }

    public static async Task WaitForIndexerAsync(LabConfig config)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var status = await GetIndexerStatusAsync(config);
            var last = status.RootElement.GetProperty("lastResult");
            var currentStatus = last.GetProperty("status").GetString();
            Console.WriteLine($"Indexer status: {currentStatus}");

            if (currentStatus is "success" or "transientFailure" or "persistentFailure")
            {
                Console.WriteLine(status.RootElement.ToString());
                return;
            }
        }
    }

    public static async Task PrintIndexerStatusAsync(LabConfig config)
    {
        var status = await GetIndexerStatusAsync(config);
        Console.WriteLine(JsonSerializer.Serialize(status.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<JsonDocument> GetIndexerStatusAsync(LabConfig config)
    {
        using var client = CreateClient(config);
        var response = await client.GetAsync(Uri(config, $"/indexers/{config.SearchIndexerName}/status"));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static HttpClient CreateClient(LabConfig config)
    {
        var client = new HttpClient { BaseAddress = config.SearchEndpoint };
        client.DefaultRequestHeaders.Add("api-key", config.SearchAdminKey);
        return client;
    }

    private static async Task PutAsync(HttpClient client, LabConfig config, string path, object body)
    {
        var json = JsonSerializer.Serialize(body, Json.Options);
        var response = await client.PutAsync(Uri(config, path), new StringContent(json, Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static string Uri(LabConfig config, string path) => $"{path}?api-version={ApiVersion}";

    private static object BuildIndex(LabConfig config) => new
    {
        name = config.SearchIndexName,
        fields = new object[]
        {
            new { name = "id", type = "Edm.String", key = true, filterable = true, sortable = false, searchable = false },
            new { name = "metadata_storage_name", type = "Edm.String", searchable = true, filterable = true, retrievable = true },
            new { name = "metadata_storage_path", type = "Edm.String", searchable = false, filterable = false, retrievable = true },
            new { name = "content", type = "Edm.String", searchable = true, retrievable = true },
            new { name = "merged_content", type = "Edm.String", searchable = true, retrievable = true },
            new { name = "ocrText", type = "Collection(Edm.String)", searchable = true, retrievable = true },
            new { name = "ocrLayoutText", type = "Collection(Edm.String)", searchable = true, retrievable = true },
            new { name = "imageDescription", type = "Collection(Edm.String)", searchable = true, retrievable = true },
            new { name = "contentVector", type = "Collection(Edm.Single)", searchable = true, retrievable = false, dimensions = 1536, vectorSearchProfile = "aoai-vector-profile" }
        },
        vectorSearch = new
        {
            algorithms = new[] { new { name = "hnsw", kind = "hnsw" } },
            profiles = new[] { new { name = "aoai-vector-profile", algorithm = "hnsw" } }
        },
        semantic = new
        {
            configurations = new[]
            {
                new
                {
                    name = "semantic-config",
                    prioritizedFields = new
                    {
                        titleField = new { fieldName = "metadata_storage_name" },
                        prioritizedContentFields = new object[]
                        {
                            new { fieldName = "merged_content" },
                            new { fieldName = "imageDescription" },
                            new { fieldName = "ocrText" },
                            new { fieldName = "content" }
                        },
                        prioritizedKeywordsFields = Array.Empty<object>()
                    }
                }
            }
        }
    };

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
                ("defaultLanguageCode", "en"),
                ("detectOrientation", true),
                ("inputs", new[] { new { name = "image", source = "/document/normalized_images/*" } }),
                ("outputs", new[]
                {
                    new { name = "text", targetName = "text" },
                    new { name = "layoutText", targetName = "layoutText" }
                })),
            Obj(
                ("@odata.type", "#Microsoft.Skills.Custom.ChatCompletionSkill"),
                ("name", "verbalize-images-with-llm"),
                ("description", "Describe each normalized image with a multimodal LLM instead of Azure AI Vision ImageAnalysisSkill."),
                ("context", "/document/normalized_images/*"),
                ("uri", $"{config.OpenAiEndpoint.ToString().TrimEnd('/')}/openai/deployments/{config.ChatDeploymentName}/chat/completions?api-version=2024-10-21"),
                ("commonModelParameters", new { temperature = 0.2, maxTokens = 350 }),
                ("responseFormat", new { type = "text" }),
                ("inputs", new object[]
                {
                    new { name = "image", source = "/document/normalized_images/*/data" },
                    new { name = "imageDetail", source = "='high'" },
                    new { name = "systemMessage", source = "='You verbalize enterprise document images for retrieval. Be factual and include visible text, chart labels, people names, owners, risks, policies, process steps, and org relationships. Do not invent facts.'" },
                    new { name = "userMessage", source = "='Describe this image for an enterprise search index. Include any visible words and why the image matters.'" }
                }),
                ("outputs", new[] { new { name = "response", targetName = "imageDescription" } })),
            Obj(
                ("@odata.type", "#Microsoft.Skills.Text.MergeSkill"),
                ("name", "merge-ocr"),
                ("context", "/document"),
                ("insertPreTag", "\n[OCR image text] "),
                ("insertPostTag", "\n"),
                ("inputs", new object[]
                {
                    new { name = "text", source = "/document/content" },
                    new { name = "itemsToInsert", source = "/document/normalized_images/*/text" },
                    new { name = "offsets", source = "/document/normalized_images/*/contentOffset" }
                }),
                ("outputs", new[] { new { name = "mergedText", targetName = "merged_ocr_text" } })),
            Obj(
                ("@odata.type", "#Microsoft.Skills.Text.MergeSkill"),
                ("name", "merge-image-descriptions"),
                ("context", "/document"),
                ("insertPreTag", "\n[GenAI image description] "),
                ("insertPostTag", "\n"),
                ("inputs", new object[]
                {
                    new { name = "text", source = "/document/merged_ocr_text" },
                    new { name = "itemsToInsert", source = "/document/normalized_images/*/imageDescription" },
                    new { name = "offsets", source = "/document/normalized_images/*/contentOffset" }
                }),
                ("outputs", new[] { new { name = "mergedText", targetName = "merged_all" } })),
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

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);

    private static object BuildDataSource(LabConfig config) => new
    {
        name = config.SearchDataSourceName,
        type = "azureblob",
        credentials = new { connectionString = $"ResourceId={config.StorageAccountId};" },
        container = new { name = config.BlobContainerName }
    };

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
}

static class SearchValidator
{
    public static async Task ValidateAsync(LabConfig config)
    {
        using var client = SearchHttpClient(config);
        var body = new
        {
            search = "policy supplier org chart badge access",
            count = true,
            queryType = "semantic",
            semanticConfiguration = "semantic-config",
            captions = "extractive|highlight-true",
            select = "metadata_storage_name,merged_content,ocrText,imageDescription",
            top = 10
        };

        var json = await PostSearchAsync(client, config, body);
        Console.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("value").EnumerateArray().ToArray();
        var missingImageOutputs = rows.Where(row =>
            !row.TryGetProperty("ocrText", out var ocr) || ocr.GetArrayLength() == 0 ||
            !row.TryGetProperty("imageDescription", out var descriptions) || descriptions.GetArrayLength() == 0).ToArray();

        if (rows.Length == 0)
        {
            throw new InvalidOperationException("Search returned no documents. Check blob upload and indexer status.");
        }

        if (missingImageOutputs.Length > 0)
        {
            Console.WriteLine("Some documents have empty OCR or imageDescription. Text-only source files can be expected, but image-bearing sample files should be populated.");
        }
    }

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

    private static HttpClient SearchHttpClient(LabConfig config)
    {
        var client = new HttpClient { BaseAddress = config.SearchEndpoint };
        client.DefaultRequestHeaders.Add("api-key", config.SearchAdminKey);
        return client;
    }

    private static async Task<string> PostSearchAsync(HttpClient client, LabConfig config, object body)
    {
        var uri = $"/indexes/{config.SearchIndexName}/docs/search?api-version=2026-04-01";
        var response = await client.PostAsync(uri, new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Search failed: {(int)response.StatusCode} {json}");
        }

        return json;
    }
}

static class Embeddings
{
    private static readonly string[] CognitiveScope = ["https://cognitiveservices.azure.com/.default"];

    public static async Task<float[]> GetEmbeddingAsync(LabConfig config, string text)
    {
        using var client = new HttpClient { BaseAddress = config.OpenAiEndpoint };
        var token = await new DefaultAzureCredential().GetTokenAsync(new Azure.Core.TokenRequestContext(CognitiveScope));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var request = new { input = text, dimensions = 1536 };
        var uri = $"/openai/deployments/{config.EmbeddingDeploymentName}/embeddings?api-version=2024-10-21";
        var response = await client.PostAsync(uri, new StringContent(JsonSerializer.Serialize(request, Json.Options), Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Embedding failed: {(int)response.StatusCode} {json}");
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
}

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

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
