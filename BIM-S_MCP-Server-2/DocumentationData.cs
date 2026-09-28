using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using ModelContextProtocol.Protocol;

namespace BimS.Mcp2;

public sealed record DocumentationScope(string Kind, long[] SheetIds, long[] ViewIds, bool IncludeTemplates)
{
    public static DocumentationScope Create(string scope, long[]? sheetIds, long[]? viewIds, bool includeTemplates)
    {
        var sheets = (sheetIds ?? []).Distinct().Order().ToArray();
        var views = (viewIds ?? []).Distinct().Order().ToArray();
        if (sheets.Concat(views).Any(id => id <= 0) || scope switch
        {
            "document" => sheets.Length != 0 || views.Length != 0,
            "sheets" => sheets.Length == 0 || views.Length != 0,
            "views" => views.Length == 0 || sheets.Length != 0,
            _ => true
        }) throw new InvalidDataException("scope=document без фильтров; sheets только с sheetIds; views только с viewIds.");
        return new(scope, sheets, views, includeTemplates);
    }
}

public static class DocumentationData
{
    public const string Server = "BIM-S_MCP-Server-2";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };
    public static readonly string[] EntityArrays = ["sheets", "views", "placements", "elements"];
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Json)!;
    public static string Session(string? value) => Guid.TryParseExact(value, "N", out _)
        ? value! : throw new InvalidDataException("Требуется documentSession GUID N из discovery tool.");
    public static JsonObject Object(string json) => JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidDataException("Ожидается JSON object.");
    public static JsonArray Array(JsonObject root, string key) => root[key] as JsonArray
        ?? throw new InvalidDataException("Отсутствует массив " + key);
    public static string Text(JsonObject root, string key) => root[key]?.GetValue<string>()
        ?? throw new InvalidDataException("Отсутствует строка " + key);
    public static long Id(JsonObject row, string key = "elementId") => row[key]?.GetValue<long>() is > 0 and var id
        ? id : throw new InvalidDataException("Некорректный " + key);
    public static JsonObject Counts(JsonObject graph) => new(EntityArrays.Select(key =>
        KeyValuePair.Create<string, JsonNode?>(key, JsonValue.Create(Array(graph, key).Count))));

    public static Dictionary<long, JsonObject> ValidateGraph(JsonObject root)
    {
        var all = new Dictionary<long, JsonObject>();
        if (Text(root, "status") is not ("complete" or "partial")) throw new InvalidDataException("Неверный статус графа.");
        Array(root, "errors"); Array(root, "warnings");
        foreach (var key in EntityArrays)
            foreach (var node in Array(root, key))
            {
                var row = node as JsonObject ?? throw new InvalidDataException("Неверная сущность.");
                if (!all.TryAdd(Id(row), row)) throw new InvalidDataException("Повторяющийся ID сущности.");
                if (Text(row, "status") is not ("ok" or "partial")) throw new InvalidDataException("Неверный статус сущности.");
                Text(row, "class"); Text(row, "kind");
                var errors = Array(row, "errors");
                if (row["properties"] is not JsonObject properties) throw new InvalidDataException("Нет properties.");
                foreach (var property in properties)
                {
                    var value = property.Value as JsonObject ?? throw new InvalidDataException("Неверное свойство.");
                    var status = Text(value, "status");
                    if (status is not ("ok" or "noValue" or "notApplicable" or "unsupported" or "error") || !value.ContainsKey("value"))
                        throw new InvalidDataException("Неверный статус свойства.");
                    if (status == "error" && Text(row, "status") == "ok") throw new InvalidDataException("Скрытая ошибка свойства.");
                }
                if (Text(row, "status") == "ok" && errors.Count != 0) throw new InvalidDataException("Скрытая ошибка сущности.");
            }
        var relations = new HashSet<string>();
        foreach (var node in Array(root, "relations"))
        {
            var relation = node as JsonObject ?? throw new InvalidDataException("Неверная связь.");
            var kind = Text(relation, "kind"); var from = Id(relation, "fromId"); var to = Id(relation, "toId");
            if (!all.ContainsKey(from) || !all.ContainsKey(to) || !relations.Add($"{kind}:{from}:{to}"))
                throw new InvalidDataException("Связь с отсутствующим элементом или повтор связи.");
        }
        if (Text(root, "status") == "complete" && (Array(root, "errors").Count != 0 || all.Values.Any(r => Text(r, "status") != "ok")))
            throw new InvalidDataException("Скрытая неполнота графа.");
        return all;
    }

    public static void ValidateSnapshot(JsonObject root)
    {
        if (root["schemaVersion"]?.GetValue<int>() != 1 || Text(root, "server") != Server)
            throw new InvalidDataException("Неподдерживаемая схема снимка MCP-2.");
        Session(Text(root, "documentSession")); Session(Text(root, "snapshotId"));
        var entities = ValidateGraph(root);
        var selection = root["scope"]?.Deserialize<DocumentationScope>(Json) ?? throw new InvalidDataException("Нет scope.");
        DocumentationScope.Create(selection.Kind, selection.SheetIds, selection.ViewIds, selection.IncludeTemplates);
        if (root["document"] is not JsonObject) throw new InvalidDataException("Нет документа.");
        var from = root["startedAtUtc"]!.GetValue<DateTime>(); var to = root["completedAtUtc"]!.GetValue<DateTime>();
        if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from > to) throw new InvalidDataException("Неверный интервал снимка.");
        var coverage = Array(root, "coverage");
        if (coverage.Count == 0 || coverage.Any(c => c?.GetValue<string>() is not ("sheets" or "views" or "elements")))
            throw new InvalidDataException("Неверная coverage снимка.");
        var requested = Array(root, "requestedElementIds").Select(n => n!.GetValue<long>()).ToArray();
        if (requested.Distinct().Count() != requested.Length || requested.Any(id => !entities.ContainsKey(id)))
            throw new InvalidDataException("Неверные запрошенные ID снимка.");
        var parameters = Array(root, "parameters");
        var returned = parameters.Select(n => Id(n as JsonObject ?? throw new InvalidDataException("Неверные параметры."), "ElementId")).ToArray();
        DocumentationParametersExport.ValidateBatch(parameters, returned);
        if (returned.Any(id => !requested.Contains(id))) throw new InvalidDataException("Лишние параметры в снимке.");
        var remaining = Array(root, "unprocessedElementIds").Select(n => n!.GetValue<long>()).Order().ToArray();
        if (!remaining.SequenceEqual(requested.Except(returned).Order()) ||
            (Text(root, "status") == "complete" && (remaining.Length > 0 || parameters.OfType<JsonObject>().Any(p => Text(p, "status") != "ok"))))
            throw new InvalidDataException("Несогласованная полнота снимка.");
    }

    public static CallToolResult Result(JsonObject data, string text, bool error = false) => new()
    {
        IsError = error, StructuredContent = JsonSerializer.SerializeToElement(data, Json),
        Content = [new TextContentBlock { Text = text }]
    };
    public static async Task<CallToolResult> Guard(string stage, Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            var message = ex switch
            {
                InvalidDataException => ex.Message,
                OperationCanceledException => "Выполнение отменено или истекло время ожидания.",
                _ => "Ошибка чтения, записи или формата данных: " + ex.GetType().Name
            };
            return Result(new() { ["status"] = "error", ["stage"] = stage, ["message"] = message }, message, true);
        }
    }
}
