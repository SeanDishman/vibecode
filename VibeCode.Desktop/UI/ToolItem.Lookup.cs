using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed record FileLookupLabel(string Operation, string Title, string Location, string Detail);

public sealed partial class ToolItem
{
    private FileLookupLabel? _lookup;
    public bool IsFileLookup => Name is "Read" or "Grep" or "Glob" or "LS";
    public FileLookupLabel Lookup => _lookup ??= BuildLookup();
    public string LookupStatus => IsError || Status == "error" ? "Failed" : IsRunning ? "Running" : "Complete";
    public string LookupAccessibleName => $"{Lookup.Operation}: {Lookup.Title}. {LookupStatus}. {Lookup.Location}";

    private FileLookupLabel BuildLookup()
    {
        var input = Input as JsonObject;
        var path = ToolFileRange.PathFromInput(input);
        var operation = Name switch { "Read" => "Read", "Grep" => "Search", "Glob" => "Find", "LS" => "List", _ => DisplayName };
        var query = Str(input?["pattern"]) ?? Str(input?["query"]) ?? Str(input?["glob_pattern"]);
        var title = Name switch
        {
            "Grep" => string.IsNullOrWhiteSpace(query) ? "Search file contents" : CompactToolPresentation.ToSingleLine(query),
            "Glob" => string.IsNullOrWhiteSpace(query) ? "Find files" : CompactToolPresentation.ToSingleLine(query),
            "LS" => string.IsNullOrWhiteSpace(path) ? "Project files" : LastPathPart(path),
            _ => string.IsNullOrWhiteSpace(path) ? "Read file" : LastPathPart(path),
        };
        var locationPath = Name == "Read" ? ParentPath(path) : path;
        var location = string.IsNullOrWhiteSpace(locationPath)
            ? string.IsNullOrWhiteSpace(path) && Name == "Read" ? "File details unavailable" : "Project folder"
            : ShortLocation(locationPath);
        var detail = operation + (string.IsNullOrWhiteSpace(path) ? "" : "\n" + path)
            + (Name is "Grep" or "Glob" && !string.IsNullOrWhiteSpace(query) ? "\n" + query : "")
            + (HasLineRange ? "\nLines " + LineRangeText : "");
        return new(operation, title, location, detail);
    }

    private static string LastPathPart(string path)
    {
        var clean = path.Trim().TrimEnd('/', '\\');
        var at = Math.Max(clean.LastIndexOf('/'), clean.LastIndexOf('\\'));
        return CompactToolPresentation.ToSingleLine(at >= 0 ? clean[(at + 1)..] : clean) is { Length: > 0 } name ? name : path;
    }

    private static string? ParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var at = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return at < 0 ? null : at == 0 ? path[..1] : path[..at];
    }

    private static string ShortLocation(string path)
    {
        var clean = CompactToolPresentation.ToSingleLine(path.Trim().TrimEnd('/', '\\'));
        var parts = clean.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 4) return clean.Length == 0 ? path : clean;
        var separator = path.Contains('\\') ? "\\" : "/";
        return "…" + separator + string.Join(separator, parts.TakeLast(4));
    }
}
