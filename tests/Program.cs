using System.Text.Json;
using System.Text.Json.Nodes;
using BimS.Mcp2;
using BimS.Revit2024;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Contains("--live-only"))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(80));
    var real = new RevitBridgeClient();
    try
    {
        Console.WriteLine("Live ping: " + await real.SendAsync("ping", limit.Token));
        var identity = DocumentationData.Object(await real.SendAsync("{\"collection\":\"document\",\"fields\":[\"SessionId\"],\"scope\":\"document\"}", limit.Token));
        if (identity.ContainsKey("error")) { Console.WriteLine("Live document unavailable: " + identity["error"]); return; }
        var liveSession = DocumentationData.Session(identity["SessionId"]!.GetValue<string>());
        Console.WriteLine("Legacy document/session contract: PASS (session value omitted)");
        var mismatch = await real.SendAsync(JsonSerializer.Serialize(new { collection = "document", fields = new[] { "SessionId" }, scope = "document", documentSession = Guid.NewGuid().ToString("N") }), limit.Token);
        Console.WriteLine("Live stale-session rejection: " + DocumentationData.Object(mismatch).ContainsKey("error"));
        var live = await new DocumentationQueries(real).SheetsAsync(limit.Token, documentSession: liveSession);
        Console.WriteLine("Live viewGraph available: " + (live.IsError != true));
        if (live.IsError == true) Console.WriteLine(string.Join(" ", live.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        else Console.WriteLine("Live graph status: " + live.StructuredContent!.Value.GetProperty("status").GetString());
    }
    catch (Exception ex) { Console.WriteLine("Live integration unavailable: " + ex.GetType().Name); }
    return;
}

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
var output = Path.Combine(workspace, "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var session = "0123456789abcdef0123456789abcdef";
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++; Console.WriteLine("PASS " + message);
}
JsonObject Data(CallToolResult result) => DocumentationData.Object(result.StructuredContent!.Value.GetRawText());
JsonObject Property(object? value) => new() { ["status"] = value == null ? "noValue" : "ok", ["value"] = value == null ? null : DocumentationData.Node(value), ["source"] = "fixture" };
JsonObject Entity(long id, string kind, long? owner = null) => new()
{
    ["elementId"] = id, ["uniqueId"] = "fixture-" + id, ["class"] = "Fixture." + kind, ["kind"] = kind,
    ["name"] = kind + " <script>alert(1)</script>", ["ownerViewId"] = owner,
    ["status"] = "ok", ["errors"] = new JsonArray(), ["properties"] = new JsonObject()
};
JsonObject Graph()
{
    var sheet = Entity(1, "sheet"); sheet["properties"]!["number"] = Property("А-001");
    var text = Entity(10, "text", 2); text["properties"]!["text"] = Property("Строка 1\nСтрока 2 <>&\"");
    var dimension = Entity(11, "dimension", 2);
    dimension["properties"]!["segments"] = Property(new[] { new { index = 0, value = 1.25, valueOverride = "замена" }, new { index = 1, value = 2.5, valueOverride = "" } });
    var tag = Entity(12, "tag", 2); tag["properties"]!["targets"] = Property(new[] { new { elementId = 99, status = "ok" }, new { elementId = 100, status = "unresolved" } });
    return new JsonObject
    {
        ["contractVersion"] = 1, ["documentSession"] = session, ["document"] = new JsonObject { ["title"] = "Test" },
        ["status"] = "complete", ["errors"] = new JsonArray(), ["warnings"] = new JsonArray("fixture, not Revit"),
        ["sheets"] = new JsonArray(sheet, Entity(4, "sheet")), ["views"] = new JsonArray(Entity(2, "view"), Entity(5, "view")),
        ["placements"] = new JsonArray(Entity(3, "viewport", 1), Entity(6, "schedulePlacement", 1)),
        ["elements"] = new JsonArray(text, dimension, tag, Entity(13, "titleBlock", 1)),
        ["relations"] = DocumentationData.Node(new[]
        {
            new { kind="onSheet", fromId=3, toId=1 }, new { kind="placedView", fromId=3, toId=2 },
            new { kind="onSheet", fromId=6, toId=1 }, new { kind="placedView", fromId=6, toId=2 },
            new { kind="ownedBy", fromId=10, toId=2 }, new { kind="ownedBy", fromId=11, toId=2 },
            new { kind="ownedBy", fromId=12, toId=2 }, new { kind="ownedBy", fromId=13, toId=1 }
        })
    };
}
JsonArray Parameters(long[] ids) => new(ids.Select(id => (JsonNode)new JsonObject
{
    ["ElementId"] = id, ["status"] = "ok", ["errors"] = new JsonArray(), ["TypeId"] = null,
    ["InstanceParameters"] = new JsonArray(new JsonObject
    {
        ["parameterId"] = -1001L, ["ownerElementId"] = id, ["source"] = "instance", ["name"] = "Text",
        ["status"] = "ok", ["errors"] = new JsonArray(), ["rawValue"] = "a\nb", ["displayValue"] = "a\nb"
    }), ["TypeParameters"] = new JsonArray()
}).ToArray());
var fixture = Graph();
var fake = new FakeBridge(request => request == "ping" ? "fixture pong" : fixture.ToJsonString());
var query = new DocumentationQueries(fake);
var discovered = await query.ElementsAsync(session, default);
Check(discovered.IsError != true, "discovery accepts valid graph");
var discovery = Data(discovered);
Check(fake.Requests.Count == 1 && fake.Requests[0].Contains("viewGraph") && !fake.Requests[0].Contains("get-documentation"), "one generic query, no tool names");
Check((await query.ViewsAsync("wrong", default)).IsError == true && fake.Requests.Count == 1, "invalid session rejected before transport");
Check((await query.SheetsAsync(default, "sheets", [1], [2])).IsError == true, "incompatible filters rejected");
fake.Reply = _ => new JsonObject { ["error"] = "Активен другой документ Revit." }.ToJsonString();
Check((await query.ElementsAsync(session, default)).IsError == true, "stale session error preserved, no rebind");
fake.Reply = _ => { var g = Graph(); g["documentSession"] = "1123456789abcdef0123456789abcdef"; return g.ToJsonString(); };
Check((await query.ElementsAsync(session, default)).IsError == true, "mismatched returned session rejected");
fake.Reply = _ => { var g = Graph(); ((JsonArray)g["elements"]!).Add(g["elements"]![0]!.DeepClone()); return g.ToJsonString(); };
Check((await query.ElementsAsync(session, default)).IsError == true, "duplicate discovery IDs rejected");
fake.Reply = _ => "[]";
Check((await query.ElementsAsync(session, default)).IsError == true, "legacy/wrong discovery response not accepted as empty graph");
fake.Reply = request =>
{
    var input = DocumentationData.Object(request);
    if (input["documentSession"]!.GetValue<string>() != session || input["collection"]!.GetValue<string>() != "elementParameters") throw new Exception("Bad request");
    return Parameters(input["elementIds"]!.GetValues()).ToJsonString();
};
fake.Requests.Clear();
var files = new ReportFiles(output);
var export = new DocumentationParametersExport(fake, files);
var context = JsonSerializer.SerializeToElement(discovery);
var exported = await export.ExportAsync([1, 2, 3, 4, 5, 6, 10, 11, 12, 13], session, [context, context], default);
Check(exported.IsError != true && fake.Requests.Count == 1, "export calls only parameters, deduplicates repeated discovery");
var saved = Data(exported)["filePath"]!.GetValue<string>();
var snapshot = DocumentationData.Object(await File.ReadAllTextAsync(saved));
Check(((JsonArray)snapshot["views"]!).Count == 2 && ((JsonArray)snapshot["placements"]!).Count == 2, "repeated placement does not duplicate view");
Check(snapshot["elements"]![0]!["properties"]!["text"]!["value"]!.GetValue<string>().Contains('\n'), "text newlines retained");
Check(snapshot["elements"]![1]!["properties"]!["segments"]!["value"]![0]!["valueOverride"]!.GetValue<string>() == "замена", "dimension segments and overrides retained");
Check(snapshot["elements"]![2]!["properties"]!["targets"]!["value"]!.AsArray().Count == 2, "multi-target and unresolved tag references retained");
var report = new DocumentationReport(files);
fake.Reply = _ => throw new Exception("Report must never access bridge");
var reported = await report.CreateAsync(saved, default);
Check(reported.IsError != true, "HTML generated offline");
var html = await File.ReadAllTextAsync(Data(reported)["filePath"]!.GetValue<string>());
Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "HTML encoded");
Check(html.Contains("Лист") && html.Contains("Виды без размещений") && html.Contains("Непосредственно на листе"), "sheet without viewport, unplaced views and sheet annotations rendered");
Check((await report.CreateAsync(Path.Combine(workspace, "outside.json"), default)).IsError == true, "report rejects outside path");
Check((await export.ExportAsync([999], session, [context], default)).IsError == true, "export rejects IDs outside discovery");
var conflicting = (JsonObject)discovery.DeepClone(); conflicting["views"]![0]!["name"] = "Changed";
Check((await export.ExportAsync([2], session, [context, JsonSerializer.SerializeToElement(conflicting)], default)).IsError == true, "conflicting discovery fails instead of mixing versions");
var many = (JsonObject)discovery.DeepClone();
for (var id = 20; id <= 30; id++) ((JsonArray)many["elements"]!).Add(Entity(id, "text", 2));
fake.Requests.Clear();
fake.Reply = request => fake.Requests.Count == 1 ? Parameters(DocumentationData.Object(request)["elementIds"]!.GetValues()).ToJsonString() : "{\"error\":\"session changed\"}";
var partial = await export.ExportAsync(Enumerable.Range(20, 11).Select(x => (long)x).ToArray(), session, [JsonSerializer.SerializeToElement(many)], default);
Check(partial.IsError == true && Data(partial)["status"]!.GetValue<string>() == "partial" && fake.Requests.Count == 2, "10 item batches, partial JSON on later bridge failure");
Check(Data(partial)["unprocessedElementIds"]!.AsArray().Count == 1, "remaining IDs recorded");
var partialHtml = await report.CreateAsync(Data(partial)["filePath"]!.GetValue<string>(), default);
Check((await File.ReadAllTextAsync(Data(partialHtml)["filePath"]!.GetValue<string>())).Contains("НЕПОЛНАЯ ВЫГРУЗКА"), "partial report visibly marked");
fake.Reply = _ => "{\"error\":\"unavailable\"}";
var countBefore = Directory.GetFiles(output).Length;
Check((await export.ExportAsync([1], session, [context], default)).IsError == true && Directory.GetFiles(output).Length == countBefore, "total parameter failure creates no snapshot");
fake.Reply = _ => { var p = Parameters([1]); p[0]!["InstanceParameters"]![0]!["ownerElementId"] = 999; return p.ToJsonString(); };
Check((await export.ExportAsync([1], session, [context], default)).IsError == true, "foreign parameter owner rejected");
foreach (var mode in new[] { "missing", "duplicate", "extra", "hiddenError" })
{
    fake.Reply = _ =>
    {
        var p = Parameters([1, 2]);
        if (mode == "missing") p.RemoveAt(1);
        if (mode == "duplicate") p[1] = p[0]!.DeepClone();
        if (mode == "extra") p[1]!["ElementId"] = 999;
        if (mode == "hiddenError") p[0]!["InstanceParameters"]![0]!["status"] = "error";
        return p.ToJsonString();
    };
    Check((await export.ExportAsync([1, 2], session, [context], default)).IsError == true, "malformed parameter batch rejected: " + mode);
}
fake.Reply = _ => new JsonArray(new JsonObject { ["ElementId"] = 1, ["status"] = "notFound", ["errors"] = new JsonArray() }).ToJsonString();
var deleted = await export.ExportAsync([1], session, [context], default);
Check(deleted.IsError == true && Data(deleted)["status"]!.ToString() == "partial", "element deleted between discovery and parameters preserved as notFound");
Check(Directory.GetFiles(output, "*.tmp").Length == 0, "atomic writes leave no temporary files");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    Check((await export.ExportAsync([1], session, [context], cancel.Token)).IsError == true, "cancelled export does not proceed");
}
fake.Reply = _ =>
{
    var g = Graph(); foreach (var key in DocumentationData.EntityArrays.Concat(new[] { "relations" })) g[key] = new JsonArray();
    return g.ToJsonString();
};
var empty = await query.SheetsAsync(default);
Check(empty.IsError != true, "confirmed empty discovery succeeds");
fake.Requests.Clear();
Check((await export.ExportAsync([], session, [empty.StructuredContent!.Value], default)).IsError != true && fake.Requests.Count == 0, "empty selection saves without invalid empty parameter query");
var parse = new Dictionary<string, object>
{
    ["collection"] = "viewGraph", ["fields"] = new object[] { "Sheets" }, ["scope"] = "sheets", ["sheetIds"] = new object[] { 1, 1 }
};
Check(ViewGraphRequest.Parse(parse).SheetIds.Length == 1, "bridge parser validates and deduplicates filters");
parse["toolName"] = "get-documentation-sheets";
try { ViewGraphRequest.Parse(parse); Check(false, "unknown bridge keys rejected"); } catch (ArgumentException) { Check(true, "unknown bridge keys rejected"); }

// Public orchestration reuses the graph query and exporter; only the fake bridge is substituted.
var combinedRoot = Path.Combine(output, "combined");
var combinedFiles = new ReportFiles(combinedRoot);
fake.Requests.Clear();
string CompleteReply(string request)
{
    var input = DocumentationData.Object(request);
    if (input["collection"]!.ToString() == "viewGraph") return Graph().ToJsonString();
    if (input["collection"]!.ToString() != "elementParameters" || input["documentSession"]!.ToString() != session)
        throw new Exception("Unexpected query or session");
    return Parameters(input["elementIds"]!.GetValues()).ToJsonString();
}
fake.Reply = CompleteReply;
var pipeline = new DocumentationPipeline(query, new DocumentationParametersExport(fake, combinedFiles), new DocumentationReport(combinedFiles), combinedFiles);
var combined = await pipeline.GetElementsAsync(default);
Check(combined.IsError != true, "combined elements bootstraps session and exports JSON");
Check(fake.Requests.Count == 2 && DocumentationData.Object(fake.Requests[0])["includeAnnotations"]!.GetValue<bool>() &&
    !DocumentationData.Object(fake.Requests[0]).ContainsKey("documentSession"), "one existing viewGraph query followed by parameters, no redundant sheet/view search");
var combinedSnapshot = DocumentationData.Object(await File.ReadAllTextAsync(Data(combined)["filePath"]!.GetValue<string>()));
DocumentationData.ValidateSnapshot(combinedSnapshot);
Check(combinedSnapshot["requestedElementIds"]!.GetValues().Order().SequenceEqual(DocumentationData.ValidateGraph(combinedSnapshot).Keys.Order()), "all graph entity IDs included in parameter export");
Check(Directory.GetFiles(combinedRoot, "*.json").Length == 1 && Directory.GetFiles(combinedRoot, "*.html").Length == 0, "elements writes exactly one JSON and no HTML");
fake.Requests.Clear();
var composed = await pipeline.GetDocumentationAsync(default, "sheets", [1], documentSession: session);
Check(composed.IsError != true && fake.Requests.Count == 2, "full composition does not reread Revit for HTML");
var composedData = Data(composed);
Check(Directory.GetFiles(combinedRoot, "*.json").Length == 2 && Directory.GetFiles(combinedRoot, "*.html").Length == 1 &&
    File.Exists(composedData["jsonPath"]!.ToString()) && File.Exists(composedData["htmlPath"]!.ToString()), "composition writes one JSON/HTML pair and returns paths");
var firstRequest = DocumentationData.Object(fake.Requests[0]);
Check(firstRequest["scope"]!.ToString() == "sheets" && firstRequest["sheetIds"]!.GetValues().SequenceEqual(new long[] { 1 }) &&
    firstRequest["documentSession"]!.ToString() == session, "scope and pinned session propagated unchanged");
fake.Requests.Clear();
Check((await pipeline.GetElementsAsync(default, documentSession: "invalid")).IsError == true && fake.Requests.Count == 0, "combined tool rejects invalid session before bridge");
Check((await pipeline.GetDocumentationAsync(default, "views", sheetIds: [1], viewIds: [2])).IsError == true && fake.Requests.Count == 0, "composition rejects incompatible filters");
fake.Reply = request =>
{
    if (DocumentationData.Object(request)["collection"]!.ToString() != "viewGraph") return CompleteReply(request);
    var graph = Graph(); graph["documentSession"] = "1123456789abcdef0123456789abcdef"; return graph.ToJsonString();
};
Check((await pipeline.GetDocumentationAsync(default, documentSession: session)).IsError == true && fake.Requests.Count == 1, "session mismatch stops before parameter collection");
var filesBefore = Directory.GetFiles(combinedRoot).Length;
fake.Reply = _ => "{\"error\":\"bridge unavailable\"}";
Check((await pipeline.GetDocumentationAsync(default)).IsError == true && Directory.GetFiles(combinedRoot).Length == filesBefore, "failed discovery creates no JSON or HTML");
fake.Reply = request =>
{
    if (DocumentationData.Object(request)["collection"]!.ToString() == "viewGraph") return Graph().ToJsonString();
    var rows = JsonNode.Parse(CompleteReply(request))!.AsArray();
    rows[0]!["status"] = "notFound"; rows[0]!.AsObject().Remove("InstanceParameters");
    return rows.ToJsonString();
};
var partialComposition = await pipeline.GetDocumentationAsync(default);
Check(partialComposition.IsError == true && Data(partialComposition)["status"]!.ToString() == "partial" &&
    File.Exists(Data(partialComposition)["jsonPath"]!.ToString()) && Directory.GetFiles(combinedRoot).Length == filesBefore + 1,
    "partial composition preserves JSON and skips HTML like MCP-1");
fake.Reply = CompleteReply;
var brokenReportPipeline = new DocumentationPipeline(query, new DocumentationParametersExport(fake, combinedFiles),
    new DocumentationReport(new ReportFiles(Path.Combine(output, "wrong-report-root"))), combinedFiles);
var failedReport = await brokenReportPipeline.GetDocumentationAsync(default);
Check(failedReport.IsError == true && Data(failedReport)["stage"]!.ToString() == "create-documentation-elements-report" &&
    File.Exists(Data(failedReport)["jsonPath"]!.ToString()), "report failure retains JSON path and exact failed stage");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); fake.Requests.Clear();
    Check((await pipeline.GetDocumentationAsync(cancelled.Token)).IsError == true && fake.Requests.Count == 0, "cancelled composition performs no bridge calls");
}
fake.Reply = _ =>
{
    var graph = Graph(); foreach (var key in DocumentationData.EntityArrays.Concat(new[] { "relations" })) graph[key] = new JsonArray();
    return graph.ToJsonString();
};
fake.Requests.Clear();
Check((await pipeline.GetDocumentationAsync(default)).IsError != true && fake.Requests.Count == 1, "confirmed empty graph produces JSON and HTML without parameter queries");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var server = Path.Combine(workspace, "BIM-S_MCP-Server-2", "bin", "Debug", "net10.0", "BIM-S_MCP-Server-2.dll");
var stderr = new List<string>();
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "MCP2 tests", Command = "dotnet", Arguments = [server], WorkingDirectory = workspace,
    StandardErrorLines = line => stderr.Add(line)
}), cancellationToken: deadline.Token);
var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
string[] expected = ["revit-documentation-ping", "get-documentation-elements", "create-documentation-elements-report", "get-documentation"];
Check(tools.Select(t => t.Name).Order().SequenceEqual(expected.Order()), "real MCP handshake and exactly four tools on clean stdout");
Check(!tools.Any(t => new[] { "get-documentation-sheets", "get-documentation-views", "get-documentation-elements-parameters", "create-documentation-report" }.Contains(t.Name)), "all obsolete public tools absent");
await File.WriteAllTextAsync(Path.Combine(output, "tool-schemas.json"), JsonSerializer.Serialize(tools.Select(t => new { t.Name, t.JsonSchema }), DocumentationData.Json));
var invalidCall = await client.CallToolAsync("get-documentation-elements", new Dictionary<string, object?> { ["documentSession"] = "invalid" }, cancellationToken: deadline.Token);
Check(invalidCall.IsError == true && Data(invalidCall)["stage"]!.ToString() == "get-documentation-elements", "MCP invalid session returns structured stage error");
var invalidPipeline = await client.CallToolAsync("get-documentation", new Dictionary<string, object?> { ["documentSession"] = "invalid" }, cancellationToken: deadline.Token);
Check(invalidPipeline.IsError == true, "public get-documentation handles invalid session");
var offlineCall = await client.CallToolAsync("create-documentation-elements-report", new Dictionary<string, object?> { ["filePath"] = saved }, cancellationToken: deadline.Token);
Check(offlineCall.IsError == true, "production server isolates report root from test directory");
Check(stderr.Count == 0, "no unexpected server diagnostics");
// Smoke-test the existing MCP-1 binary without rebuilding or changing its files.
await using var modelClient = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "MCP1 regression", Command = "dotnet",
    Arguments = [@"D:\BIM-S-MCP-1\BIM-S_MCP-Server\bin\Debug\net10.0\BIM-S_MCP-Server.dll"],
    WorkingDirectory = @"D:\BIM-S-MCP-1\BIM-S_MCP-Server"
}), cancellationToken: deadline.Token);
var modelTools = await modelClient.ListToolsAsync(cancellationToken: deadline.Token);
string[] modelExpected = ["get-model", "create-model-elements-report", "get-model-elements-parameters", "start-model-watch", "stop-model-watch", "get-model-summary", "get-model-elements", "revit-ping"];
Check(modelTools.Select(t => t.Name).Order().SequenceEqual(modelExpected.Order()), "unchanged MCP-1 starts and exposes its original eight tools");
var rejectedModel = await modelClient.CallToolAsync("get-model-elements-parameters", new Dictionary<string, object?>
    { ["elementIds"] = new long[] { 1 }, ["documentSession"] = "invalid" }, cancellationToken: deadline.Token);
Check(rejectedModel.IsError == true, "unchanged MCP-1 parameter validation still works");
Console.WriteLine($"{passed} checks passed. Fixtures only; no live Revit integration. Artifacts: {output}");

sealed class FakeBridge(Func<string, string> reply) : IRevitBridge
{
    public Func<string, string> Reply = reply;
    public List<string> Requests { get; } = [];
    public Task<string> SendAsync(string request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Reply(request)); }
}
static class JsonExtensions
{
    public static long[] GetValues(this JsonNode node) => node.AsArray().Select(n => n!.GetValue<long>()).ToArray();
}
