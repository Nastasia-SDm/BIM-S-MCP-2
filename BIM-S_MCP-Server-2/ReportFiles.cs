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
            !Path.GetFileName(full).EndsWith("_documentation.json", StringComparison.Ordinal) ||
            !File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Файл должен быть выгрузкой MCP-2 в каталоге отчётов.");
        return full;
    }

    public async Task<string> SaveAsync(string extension, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);

        const string modelKey = "Test_AI-Work";

        var existingVersions = Directory
            .EnumerateFiles(Root, $"{modelKey}_V*_documentation.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name =>
            {
                var marker = name?.Split("_V", StringSplitOptions.None).LastOrDefault();
                return int.TryParse(marker?.Replace("_documentation", ""), out var version)
                    ? version
                    : 0;
            })
            .ToArray();

        var nextVersion = existingVersions.Length == 0
            ? 1
            : existingVersions.Max() + 1;

        var versionName = $"V{nextVersion:000}";
        var path = Path.Combine(
            Root,
            $"{modelKey}_{versionName}_documentation{extension}");

        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return path;
    }
}
