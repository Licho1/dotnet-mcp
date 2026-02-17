using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var serverPath = Path.Combine(repoRoot, "src", "DotnetMcp", "DotnetMcp.csproj");

Console.WriteLine($"Connecting to server: {serverPath}");

var stderrLines = new List<string>();
var transport = new StdioClientTransport(new()
{
    Command = "dotnet",
    Arguments = ["run", "--project", serverPath, "--no-build"],
    StandardErrorLines = line => stderrLines.Add(line)
});

await using var client = await McpClient.CreateAsync(transport);
Console.WriteLine($"Connected! Server: {client.ServerInfo?.Name} v{client.ServerInfo?.Version}");

var tools = await client.ListToolsAsync();
Console.WriteLine($"\nAvailable tools ({tools.Count}):");
foreach (var tool in tools)
    Console.WriteLine($"  - {tool.Name}");

string GetText(CallToolResult r) =>
    (r.IsError == true ? "ERROR: " : "") +
    (r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no text)");

async Task Test(string label, string tool, Dictionary<string, object?> args)
{
    Console.WriteLine($"\n=== {label} ===");
    stderrLines.Clear();
    var result = await client.CallToolAsync(tool, args);
    var text = GetText(result);
    Console.WriteLine(text);
    if (result.IsError == true)
    {
        foreach (var line in stderrLines.Where(l => l.Contains("fail:") || l.Contains("Exception") || l.Contains("   at ")))
            Console.WriteLine($"  {line}");
    }
}

// === Load ===

await Test("Load Project", "load", new() { ["path"] = serverPath });

// === Type Hierarchy (now includes members) ===

await Test("Type Hierarchy: WorkspaceService", "type-hierarchy", new()
{
    ["typeName"] = "WorkspaceService"
});

// === Find References ===

await Test("Find References: WorkspaceService", "find-references", new()
{
    ["symbolName"] = "WorkspaceService"
});

// === Find Implementations ===

await Test("Find Implementations: IDisposable", "find-implementations", new()
{
    ["typeName"] = "IDisposable"
});

// === Get Source (by name) ===

await Test("Get Source (by name): WorkspaceService", "get-source", new()
{
    ["symbolName"] = "WorkspaceService"
});

await Test("Get Source (by name, method): LoadSolutionAsync", "get-source", new()
{
    ["symbolName"] = "LoadSolutionAsync",
    ["kind"] = "method"
});

// === Get Source (by location — metadata symbol → SourceLink/decompile) ===

await Test("Get Source (by location, metadata symbol)", "get-source", new()
{
    ["filePath"] = Path.Combine(repoRoot, "src", "DotnetMcp", "Services", "WorkspaceService.cs"),
    ["line"] = 32,
    ["column"] = 33
});

// === Find Callers ===

await Test("Find Callers: FindSymbolsAsync", "find-callers", new()
{
    ["methodName"] = "FindSymbolsAsync",
    ["typeName"] = "WorkspaceService"
});

Console.WriteLine("\n\nAll tests completed!");
