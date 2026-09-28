using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BimS.Mcp2;

public sealed class DocumentationQueries(IRevitBridge bridge)
{
    public Task<CallToolResult> PingAsync(CancellationToken cancellationToken) => DocumentationData.Guard("ping", async () =>
    {
        var response = await bridge.SendAsync("ping", cancellationToken);
        return DocumentationData.Result(new() { ["status"] = "complete", ["response"] = response }, response);
    });
    public Task<CallToolResult> SheetsAsync(CancellationToken cancellationToken, string scope = "document",
        long[]? sheetIds = null, long[]? viewIds = null, string? documentSession = null, bool includeTemplates = false) =>
        Query("sheets", documentSession, scope, sheetIds, viewIds, includeTemplates, cancellationToken);
    public Task<CallToolResult> ViewsAsync(string documentSession, CancellationToken cancellationToken,
        string scope = "document", long[]? sheetIds = null, long[]? viewIds = null, bool includeTemplates = false) =>
        Query("views", documentSession, scope, sheetIds, viewIds, includeTemplates, cancellationToken);
    public Task<CallToolResult> ElementsAsync(string documentSession, CancellationToken cancellationToken,
        string scope = "document", long[]? sheetIds = null, long[]? viewIds = null, bool includeTemplates = false) =>
        Query("elements", documentSession, scope, sheetIds, viewIds, includeTemplates, cancellationToken);

    // The same graph reader already includes sheets, views and placements. Bootstrap its
    // session in this one request rather than repeating the structural discovery queries.
    internal Task<CallToolResult> CollectAsync(string? documentSession, string scope, long[]? sheetIds,
        long[]? viewIds, bool includeTemplates, CancellationToken cancellationToken) =>
        Query("elements", documentSession, scope, sheetIds, viewIds, includeTemplates, cancellationToken, allowInitialSession: true);

    private Task<CallToolResult> Query(string coverage, string? session, string scope, long[]? sheetIds,
        long[]? viewIds, bool includeTemplates, CancellationToken ct, bool allowInitialSession = false) => DocumentationData.Guard("get-documentation-" + coverage, async () =>
    {
        ct.ThrowIfCancellationRequested();
        var selection = DocumentationScope.Create(scope, sheetIds, viewIds, includeTemplates);
        if (session != null || (coverage != "sheets" && !allowInitialSession)) DocumentationData.Session(session);
        var started = DateTime.UtcNow;
        var request = new JsonObject
        {
            ["collection"] = "viewGraph", ["fields"] = DocumentationData.Node(new[] { "Sheets", "Views", "Placements", "Elements", "Relations" }),
            ["scope"] = selection.Kind, ["includeTemplates"] = includeTemplates, ["includeAnnotations"] = coverage == "elements"
        };
        if (session != null) request["documentSession"] = session;
        if (scope == "sheets") request["sheetIds"] = DocumentationData.Node(selection.SheetIds);
        if (scope == "views") request["viewIds"] = DocumentationData.Node(selection.ViewIds);
        var data = DocumentationData.Object(await bridge.SendAsync(request.ToJsonString(), ct));
        if (data.ContainsKey("error")) throw new InvalidDataException("Bridge: " + data["error"]?.ToString());
        if (data["contractVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Bridge не поддерживает viewGraph v1. Требуется обновление add-in.");
        var returned = DocumentationData.Session(DocumentationData.Text(data, "documentSession"));
        if (session != null && returned != session) throw new InvalidDataException("Bridge вернул другую сессию.");
        DocumentationData.ValidateGraph(data);
        if (data["document"] is not JsonObject) throw new InvalidDataException("Нет сведений о документе.");
        data["schemaVersion"] = 1; data["coverage"] = coverage;
        data["scope"] = DocumentationData.Node(selection);
        data["startedAtUtc"] = DocumentationData.Node(started);
        data["completedAtUtc"] = DocumentationData.Node(DateTime.UtcNow);
        data["counts"] = DocumentationData.Counts(data);
        return DocumentationData.Result(data, $"Документация: {coverage}; статус: {data["status"]}; сессия: {returned}.");
    });
}
