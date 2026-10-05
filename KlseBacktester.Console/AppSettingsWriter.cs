using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KlseBacktester.Models;

namespace KlseBacktester.Console;

public static class AppSettingsWriter
{
    private const string FileName = "appsettings.json";
    private const string ProjectFileName = "KlseBacktester.Console.csproj";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Replace the KlseStocks array in the source appsettings.json (so the change survives
    /// rebuilds) and in the output-directory copy (so the running app and next launch agree).
    /// Returns the paths that were written.
    /// </summary>
    public static IReadOnlyList<string> WriteKlseStocks(IEnumerable<StockDefinition> stocks)
    {
        var stocksNode = new JsonArray(stocks
            .Select(s => (JsonNode)new JsonObject
            {
                ["Ticker"] = s.Ticker,
                ["Name"] = s.Name,
                ["Sector"] = s.Sector,
            })
            .ToArray());

        var paths = new[] { FindSourceSettingsPath(), Path.Combine(AppContext.BaseDirectory, FileName) }
            .Where(p => p != null && File.Exists(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in paths)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
            root["KlseStocks"] = stocksNode.DeepClone();
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
        }

        return paths;
    }

    private static string? FindSourceSettingsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ProjectFileName)))
                return Path.Combine(dir.FullName, FileName);
        }

        return null;
    }
}
