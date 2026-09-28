using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BimS.Mcp2;

public sealed class DocumentationReport(ReportFiles files)
{
    private static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "не определено");
    public Task<CallToolResult> CreateAsync(string filePath, CancellationToken cancellationToken) =>
        DocumentationData.Guard("create-documentation-elements-report", async () =>
        {
            var source = files.Validate(filePath, ".json");
            var data = DocumentationData.Object(await File.ReadAllTextAsync(source, cancellationToken));
            var html = Render(data);
            var path = await files.SaveAsync(".html", html, cancellationToken);
            return DocumentationData.Result(new()
            {
                ["status"] = data["status"]!.DeepClone(), ["documentSession"] = data["documentSession"]!.DeepClone(),
                ["scope"] = data["scope"]!.DeepClone(), ["counts"] = DocumentationData.Counts(data),
                ["filePath"] = path, ["sourceFilePath"] = source
            }, "HTML: " + path);
        });

    public static string Render(JsonObject data)
    {
        DocumentationData.ValidateSnapshot(data);
        var parameters = DocumentationData.Array(data, "parameters").OfType<JsonObject>()
            .ToDictionary(p => DocumentationData.Id(p, "ElementId"));
        var html = new StringBuilder("<!doctype html><html lang='ru'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>BIM-S — Документация</title><style>");
        html.Append("*{box-sizing:border-box}body{margin:0;background:#eef2f6;color:#17243a;font:16px/1.5 Segoe UI,Arial,sans-serif}main{max-width:1400px;margin:48px auto;padding:0 24px}header{background:#13283f;color:white;padding:32px;border-radius:20px}h1{margin:0;font-size:30px}.total{font-size:34px;font-weight:700;color:#73e1c0}.table{margin-top:24px;background:white;border-radius:16px;overflow:auto;padding:20px;box-shadow:0 8px 30px #13283f12}table{width:100%;border-collapse:collapse}th,td{padding:12px;border-bottom:1px solid #edf0f4;vertical-align:top;text-align:left}th{background:#dfe8f1}tr:hover{background:#f3faf8}summary{cursor:pointer;color:#14765e}pre{white-space:pre-wrap;overflow-wrap:anywhere}.warning{background:#fff0cf;color:#6f3900;padding:16px;border-radius:12px}@media print{.table{overflow:visible}header{color:#17243a;background:white}}");
        html.Append("</style></head><body><main><header><h1>BIM-S — Документация Revit</h1><p>Аналитический отчёт</p><div class='total'>");
        html.Append($"Листов: {DocumentationData.Array(data, "sheets").Count} · Видов: {DocumentationData.Array(data, "views").Count} · Элементов: {DocumentationData.Array(data, "elements").Count}</div></header>");
        if (DocumentationData.Text(data, "status") != "complete") html.Append("<p class='warning'>НЕПОЛНАЯ ВЫГРУЗКА: сведения получены частично. Проверьте ошибки и необработанные ID.</p>");
        html.Append("<p>Отбор по владельцу-виду; фактическая видимость на печати не вычислялась. Полнота относится только к указанной области и coverage.</p>");
        var views = DocumentationData.Array(data, "views").OfType<JsonObject>().ToDictionary(r => DocumentationData.Id(r));
        var placements = DocumentationData.Array(data, "placements").OfType<JsonObject>().ToArray();
        var elements = DocumentationData.Array(data, "elements").OfType<JsonObject>().ToArray();
        var relations = DocumentationData.Array(data, "relations").OfType<JsonObject>().ToArray();
        long? Target(long id, string kind) => relations.FirstOrDefault(r => r["kind"]?.ToString() == kind && r["fromId"]?.GetValue<long>() == id)?["toId"]?.GetValue<long>();
        void Row(JsonObject row)
        {
            var id = DocumentationData.Id(row);
            html.Append($"<details><summary>{E(row["kind"])} — {E(row["name"])} · ID {id}</summary><pre>{E(row.ToJsonString(DocumentationData.Json))}</pre>");
            if (parameters.TryGetValue(id, out var p)) html.Append($"<details><summary>Параметры</summary><pre>{E(p.ToJsonString(DocumentationData.Json))}</pre></details>");
            html.Append("</details>");
        }
        foreach (var sheet in DocumentationData.Array(data, "sheets").OfType<JsonObject>())
        {
            var id = DocumentationData.Id(sheet);
            html.Append($"<section class='table'><h2>Лист {E(sheet["properties"]?["number"]?["value"])} — {E(sheet["name"])}</h2>");
            Row(sheet);
            foreach (var placement in placements.Where(p => Target(DocumentationData.Id(p), "onSheet") == id))
            {
                Row(placement);
                var viewId = Target(DocumentationData.Id(placement), "placedView");
                if (viewId.HasValue) html.Append($"<p><a href='#view-{viewId}'>Вид {E(views[viewId.Value]["name"])} · {viewId}</a></p>");
            }
            html.Append("<h3>Непосредственно на листе</h3>");
            foreach (var element in elements.Where(e => e["ownerViewId"]?.GetValue<long>() == id)) Row(element);
            html.Append("</section>");
        }
        var placed = relations.Where(r => r["kind"]?.ToString() == "placedView").Select(r => r["toId"]!.GetValue<long>()).ToHashSet();
        foreach (var group in views.Values.GroupBy(v => placed.Contains(DocumentationData.Id(v))))
        {
            html.Append($"<h2>{(group.Key ? "Размещённые виды" : "Виды без размещений в выбранной области")}</h2>");
            foreach (var view in group)
            {
                var id = DocumentationData.Id(view);
                html.Append($"<section class='table' id='view-{id}'><h3>{E(view["name"])}</h3>"); Row(view);
                foreach (var element in elements.Where(e => e["ownerViewId"]?.GetValue<long>() == id)) Row(element);
                html.Append("</section>");
            }
        }
        html.Append("<section class='table'><h2>Метаданные, область, время и диагностика</h2>");
        foreach (var pair in data.Where(p => !DocumentationData.EntityArrays.Contains(p.Key) && p.Key != "parameters"))
            html.Append($"<details><summary>{E(pair.Key)}</summary><pre>{E(pair.Value?.ToJsonString(DocumentationData.Json))}</pre></details>");
        html.Append("</section><details class='table'><summary>Полные сохранённые данные (включая все параметры и связи)</summary><pre>");
        html.Append(E(data.ToJsonString(DocumentationData.Json))).Append("</pre></details></main></body></html>");
        return html.ToString();
    }
}
