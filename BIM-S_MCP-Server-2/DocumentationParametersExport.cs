using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BimS.Mcp2;

public sealed class DocumentationParametersExport(IRevitBridge bridge, ReportFiles files)
{
    public Task<CallToolResult> ExportAsync(long[] elementIds, string documentSession, JsonElement[] discoveryResults,
        CancellationToken cancellationToken) => DocumentationData.Guard("get-documentation-elements-parameters", async () =>
    {
        DocumentationData.Session(documentSession);
        if (elementIds == null || elementIds.Any(id => id <= 0)) throw new InvalidDataException("Неверные elementIds.");
        var ids = elementIds.Distinct().Order().ToArray();
        var graph = Merge(discoveryResults, documentSession);
        var entities = DocumentationData.ValidateGraph(graph);
        if (ids.Any(id => !entities.ContainsKey(id))) throw new InvalidDataException("elementIds должны принадлежать discoveryResults.");
        var parameters = new JsonArray(); var errors = DocumentationData.Array(graph, "errors");
        foreach (var batch in ids.Chunk(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = JsonSerializer.Serialize(new
                {
                    collection = "elementParameters", scope = "document", documentSession, elementIds = batch,
                    fields = new[] { "ElementId", "Category", "FamilyName", "TypeName", "TypeId", "InstanceParameters", "TypeParameters" }
                });
                var response = JsonNode.Parse(await bridge.SendAsync(request, cancellationToken));
                if (response is JsonObject failed && failed.ContainsKey("error"))
                    throw new InvalidDataException("Bridge: " + failed["error"]?.ToString());
                var rows = ValidateBatch(response, batch);
                foreach (var row in rows) parameters.Add(row!.DeepClone());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or OperationCanceledException)
            {
                errors.Add(DocumentationData.Node(new { stage = "parameters", elementIds = batch,
                    message = ex is InvalidDataException ? ex.Message : ex.GetType().Name }));
                break;
            }
        }
        var returned = parameters.OfType<JsonObject>().Select(r => DocumentationData.Id(r, "ElementId")).ToHashSet();
        var remaining = ids.Where(id => !returned.Contains(id)).ToArray();
        if (ids.Length > 0 && returned.Count == 0) throw new InvalidDataException("Параметры не получены; снимок не сохранён. " + errors.ToJsonString());
        var partial = DocumentationData.Text(graph, "status") == "partial" || remaining.Length > 0 || errors.Count > 0 ||
            parameters.OfType<JsonObject>().Any(r => DocumentationData.Text(r, "status") != "ok");
        graph["status"] = partial ? "partial" : "complete";
        graph["server"] = DocumentationData.Server; graph["snapshotId"] = Guid.NewGuid().ToString("N");
        graph["completedAtUtc"] = DocumentationData.Node(DateTime.UtcNow);
        graph["parameters"] = parameters; graph["requestedElementIds"] = DocumentationData.Node(ids);
        graph["unprocessedElementIds"] = DocumentationData.Node(remaining);
        graph["counts"] = DocumentationData.Counts(graph);
        var path = await files.SaveAsync(".json", graph.ToJsonString(DocumentationData.Json), cancellationToken);
        return DocumentationData.Result(new()
        {
            ["status"] = graph["status"]!.DeepClone(), ["documentSession"] = documentSession,
            ["scope"] = graph["scope"]!.DeepClone(), ["counts"] = graph["counts"]!.DeepClone(),
            ["filePath"] = path, ["processedElementCount"] = returned.Count, ["requestedElementCount"] = ids.Length,
            ["unprocessedElementIds"] = DocumentationData.Node(remaining), ["errors"] = errors.DeepClone()
        }, $"JSON: {path}; параметры: {returned.Count}/{ids.Length}; статус: {graph["status"]}.", partial);
    });

    public static JsonObject Merge(JsonElement[] results, string session)
    {
        if (results == null || results.Length == 0) throw new InvalidDataException("Передайте discoveryResults из StructuredContent.");
        JsonObject? graph = null;
        var entities = new Dictionary<long, (string Kind, JsonObject Row)>();
        var relations = new HashSet<string>(); var coverage = new HashSet<string>();
        var started = DateTime.MaxValue; var ended = DateTime.MinValue;
        foreach (var result in results)
        {
            var data = DocumentationData.Object(result.GetRawText());
            if (data["schemaVersion"]?.GetValue<int>() != 1 || DocumentationData.Text(data, "documentSession") != session)
                throw new InvalidDataException("Несовместимая схема или сессия discoveryResults.");
            var selection = data["scope"]?.Deserialize<DocumentationScope>(DocumentationData.Json)
                ?? throw new InvalidDataException("Нет scope.");
            var normalized = DocumentationScope.Create(selection.Kind, selection.SheetIds, selection.ViewIds, selection.IncludeTemplates);
            data["scope"] = DocumentationData.Node(normalized);
            DocumentationData.ValidateGraph(data);
            var part = DocumentationData.Text(data, "coverage");
            if (part is not ("sheets" or "views" or "elements")) throw new InvalidDataException("Неверная coverage.");
            coverage.Add(part);
            var from = data["startedAtUtc"]!.GetValue<DateTime>(); var to = data["completedAtUtc"]!.GetValue<DateTime>();
            if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from > to) throw new InvalidDataException("Неверный интервал чтения.");
            started = from < started ? from : started; ended = to > ended ? to : ended;
            if (graph == null)
            {
                graph = (JsonObject)data.DeepClone();
                foreach (var key in DocumentationData.EntityArrays.Concat(new[] { "relations", "errors", "warnings" })) graph[key] = new JsonArray();
            }
            if (!JsonNode.DeepEquals(graph["scope"], data["scope"]) || !JsonNode.DeepEquals(graph["document"], data["document"]))
                throw new InvalidDataException("Области или сведения о документе не совпадают.");
            foreach (var key in DocumentationData.EntityArrays)
                foreach (var row in DocumentationData.Array(data, key).OfType<JsonObject>())
                {
                    var id = DocumentationData.Id(row);
                    if (entities.TryGetValue(id, out var previous))
                    {
                        if (previous.Kind != key || !JsonNode.DeepEquals(previous.Row, row)) throw new InvalidDataException("Конфликт discovery для ID " + id);
                    }
                    else { entities.Add(id, (key, row)); ((JsonArray)graph[key]!).Add(row.DeepClone()); }
                }
            foreach (var relation in DocumentationData.Array(data, "relations").OfType<JsonObject>())
                if (relations.Add($"{relation["kind"]}:{relation["fromId"]}:{relation["toId"]}")) ((JsonArray)graph["relations"]!).Add(relation.DeepClone());
            foreach (var key in new[] { "errors", "warnings" })
                foreach (var diagnostic in DocumentationData.Array(data, key)) ((JsonArray)graph[key]!).Add(diagnostic?.DeepClone());
            if (DocumentationData.Text(data, "status") == "partial") graph["status"] = "partial";
        }
        graph!["coverage"] = DocumentationData.Node(coverage.Order().ToArray());
        graph["startedAtUtc"] = DocumentationData.Node(started); graph["completedAtUtc"] = DocumentationData.Node(ended);
        return graph;
    }

    public static JsonArray ValidateBatch(JsonNode? response, long[] batch)
    {
        if (response is not JsonArray rows || rows.Count != batch.Length) throw new InvalidDataException("Неполный пакет параметров.");
        var seen = new HashSet<long>();
        foreach (var node in rows)
        {
            var row = node as JsonObject ?? throw new InvalidDataException("Неверная строка параметров.");
            var id = DocumentationData.Id(row, "ElementId");
            if (!batch.Contains(id) || !seen.Add(id)) throw new InvalidDataException("Лишний или повторный ID параметров.");
            var status = DocumentationData.Text(row, "status");
            if (status is not ("ok" or "partial" or "error" or "notFound")) throw new InvalidDataException("Неверный статус параметров.");
            if (DocumentationData.Array(row, "errors").Count != 0 && status == "ok") throw new InvalidDataException("Скрытая ошибка параметров.");
            foreach (var field in new[] { "InstanceParameters", "TypeParameters" })
            {
                if (row[field] == null && status != "ok") continue;
                var keys = new HashSet<(long, long)>();
                foreach (var item in DocumentationData.Array(row, field))
                {
                    var p = item as JsonObject ?? throw new InvalidDataException("Неверный параметр.");
                    var pid = p["parameterId"]!.GetValue<long>(); var owner = DocumentationData.Id(p, "ownerElementId");
                    var expectedOwner = field == "InstanceParameters" ? id : row["TypeId"]?.GetValue<long>();
                    if (owner != expectedOwner || !keys.Add((owner, pid)) ||
                        DocumentationData.Text(p, "source") != (field == "InstanceParameters" ? "instance" : "type"))
                        throw new InvalidDataException("Неверный владелец или повтор параметра.");
                    var state = DocumentationData.Text(p, "status");
                    var errors = DocumentationData.Array(p, "errors");
                    if (state is not ("ok" or "noValue" or "error" or "partial") ||
                        (status == "ok" && (state is "error" or "partial" || errors.Count != 0)))
                        throw new InvalidDataException("Неверный статус параметра.");
                }
            }
        }
        return rows;
    }
}
