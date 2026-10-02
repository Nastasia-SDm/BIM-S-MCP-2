using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BimS.Mcp2;

// Composition only: Revit queries, parameter validation, persistence and HTML remain
// in the existing handlers. No MCP loopback or independent second graph discovery.
public sealed class DocumentationPipeline(DocumentationQueries queries, DocumentationParametersExport export,
    DocumentationReport report, ReportFiles files)
{
    public Task<CallToolResult> GetElementsAsync(CancellationToken cancellationToken, string scope = "document",
        long[]? sheetIds = null, long[]? viewIds = null, string? documentSession = null, bool includeTemplates = false) =>
        DocumentationData.Guard("get-documentation-elements", async () =>
        {
            var discovery = await queries.CollectAsync(documentSession, scope, sheetIds, viewIds, includeTemplates, cancellationToken);
            if (discovery.IsError == true) return discovery;
            var graph = Data(discovery);
            var session = DocumentationData.Session(DocumentationData.Text(graph, "documentSession"));
            var ids = DocumentationData.ValidateGraph(graph).Keys.Order().ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var result = await export.ExportAsync(ids, session, [discovery.StructuredContent!.Value], cancellationToken);
            if (result.IsError == true) return result; // Preserve partial JSON path and diagnostics.
            var saved = Data(result);
            if (DocumentationData.Text(saved, "documentSession") != session ||
                !JsonNode.DeepEquals(saved["scope"], graph["scope"]) ||
                saved["processedElementCount"]?.GetValue<int>() != ids.Length ||
                saved["requestedElementCount"]?.GetValue<int>() != ids.Length ||
                DocumentationData.Text(saved, "status") != "complete")
                throw new InvalidDataException("Выгрузка не согласована с полученным графом документации.");
            files.Validate(DocumentationData.Text(saved, "filePath"), ".json");
            return result;
        });

    public async Task<CallToolResult> GetDocumentationAsync(CancellationToken cancellationToken, string scope = "document",
        long[]? sheetIds = null, long[]? viewIds = null, string? documentSession = null, bool includeTemplates = false)
    {
        string? jsonPath = null;
        var stage = "get-documentation-elements";
        var result = await DocumentationData.Guard("get-documentation", async () =>
        {
            var collected = await GetElementsAsync(cancellationToken, scope, sheetIds, viewIds, documentSession, includeTemplates);
            var saved = Data(collected);
            if (saved["filePath"] is { } path) jsonPath = files.Validate(path.GetValue<string>(), ".json");
            if (collected.IsError == true) return collected;
            if (jsonPath == null) throw new InvalidDataException("Этап сбора не вернул JSON.");
            stage = "create-documentation-elements-report";
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = await report.CreateAsync(jsonPath, cancellationToken);
            if (rendered.IsError == true) return rendered;
            var html = Data(rendered);
            var htmlPath = files.Validate(DocumentationData.Text(html, "filePath"), ".html");
            if (!string.Equals(files.Validate(DocumentationData.Text(html, "sourceFilePath"), ".json"), jsonPath, StringComparison.OrdinalIgnoreCase) ||
                DocumentationData.Text(html, "documentSession") != DocumentationData.Text(saved, "documentSession") ||
                !JsonNode.DeepEquals(html["scope"], saved["scope"]) ||
                DocumentationData.Text(html, "status") != "complete")
                throw new InvalidDataException("HTML не соответствует сохранённому состоянию документации.");
            var data = (JsonObject)saved.DeepClone();
            data.Remove("filePath");
            data["jsonPath"] = jsonPath; data["htmlPath"] = htmlPath;
            data["snapshot"] = DocumentationData.Object(
                await File.ReadAllTextAsync(jsonPath, cancellationToken));
            return DocumentationData.Result(data,
                $"Статус: complete; обработано: {saved["processedElementCount"]}; JSON: {jsonPath}; HTML: {htmlPath}");
        });
        if (result.IsError != true) return result;
        var failed = Data(result);
        // Keep the internal failure cause as well as the public composition stage.
        if (failed["stage"] is { } internalStage) failed["causeStage"] = internalStage.DeepClone();
        failed["stage"] = stage;
        if (jsonPath != null) failed["jsonPath"] = jsonPath;
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return DocumentationData.Result(failed, $"Ошибка этапа {stage}: {text}" +
            (jsonPath == null ? "" : " JSON: " + jsonPath), true);
    }

    private static JsonObject Data(CallToolResult result) => result.StructuredContent is { } data
        ? DocumentationData.Object(data.GetRawText()) : throw new InvalidDataException("Отсутствует StructuredContent.");
}
