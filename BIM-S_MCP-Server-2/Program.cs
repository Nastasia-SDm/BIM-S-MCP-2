using BimS.Mcp2;
using ModelContextProtocol.Server;

var bridge = new RevitBridgeClient();
var files = new ReportFiles();
var queries = new DocumentationQueries(bridge);
var export = new DocumentationParametersExport(bridge, files);
var report = new DocumentationReport(files);
var pipeline = new DocumentationPipeline(queries, export, report, files);
var options = new McpServerOptions
{
    ServerInfo = new() { Name = DocumentationData.Server, Version = "2.0.0" },
    ToolCollection =
    [
        McpServerTool.Create(queries.PingAsync, new() { Name = "revit-documentation-ping", Description = "Проверяет доступность общего Revit bridge." }),
        McpServerTool.Create(pipeline.GetElementsAsync, new() { Name = "get-documentation-elements", Description = "Получает листы, виды, размещения, аннотации и системные параметры; сохраняет общее состояние в один JSON." }),
        McpServerTool.Create(report.CreateAsync, new() { Name = "create-documentation-elements-report", Description = "Создаёт автономный HTML в стиле BIM-S из сохранённого JSON, без Revit." }),
        McpServerTool.Create(pipeline.GetDocumentationAsync, new() { Name = "get-documentation", Description = "Последовательно создаёт JSON документации и HTML-отчёт; возвращает пути к обоим файлам." })
    ]
};
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// stdout is exclusively owned by the MCP transport.
await using var server = McpServer.Create(new StdioServerTransport(options), options);
try { await server.RunAsync(shutdown.Token); }
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
