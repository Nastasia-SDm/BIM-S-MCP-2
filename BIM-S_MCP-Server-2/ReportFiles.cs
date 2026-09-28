using System.Text;

namespace BimS.Mcp2;

public sealed class ReportFiles
{
    public const string DefaultRoot = @"D:\BIM-S-MCP-2_Отчеты_Версии модели";
    public string Root { get; }
    public ReportFiles(string? root = null) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot));
    public string Validate(string path, string extension)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("documentation_", StringComparison.Ordinal) ||
            !File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Файл должен быть выгрузкой MCP-2 в каталоге отчётов.");
        return full;
    }
    public async Task<string> SaveAsync(string extension, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, $"documentation_{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}_{Guid.NewGuid():N}{extension}");
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested(); File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
