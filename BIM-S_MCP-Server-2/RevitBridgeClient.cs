using System.IO.Pipes;
using System.Text;

namespace BimS.Mcp2;

public interface IRevitBridge
{
    Task<string> SendAsync(string request, CancellationToken cancellationToken);
}

public sealed class RevitBridgeClient : IRevitBridge
{
    public async Task<string> SendAsync(string request, CancellationToken cancellationToken)
    {
        if (request.Length >= 60000 || request.Contains('\n') || request.Contains('\r'))
            throw new InvalidDataException("Запрос bridge превышает лимит или содержит перенос строки.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request == "ping" ? 5 : 60));
        await using var pipe = new NamedPipeClientStream(".", "BIMS_REVIT_BRIDGE", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
        var response = await reader.ReadLineAsync(timeout.Token);
        return !string.IsNullOrWhiteSpace(response) ? response : throw new IOException("Bridge не вернул ответ.");
    }
}
