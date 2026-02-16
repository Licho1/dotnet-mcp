using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var serverPath = Path.GetFullPath(@"C:\work\dotnet-mcp\src\DotnetMcp\DotnetMcp.csproj");

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

await Test("Load Project", "load-project", new() { ["projectPath"] = serverPath });

await Test("Find Symbol: WorkspaceService", "find-symbol", new()
{
    ["name"] = "WorkspaceService"
});

await Test("Find Symbol (kind=method): LoadSolutionAsync", "find-symbol", new()
{
    ["name"] = "LoadSolutionAsync",
    ["kind"] = "method"
});

await Test("Type Hierarchy: WorkspaceService", "type-hierarchy", new()
{
    ["typeName"] = "WorkspaceService"
});

await Test("List Members: WorkspaceService", "list-members", new()
{
    ["typeName"] = "WorkspaceService",
    ["includeInherited"] = false
});

await Test("Find References: WorkspaceService", "find-references", new()
{
    ["symbolName"] = "WorkspaceService"
});

await Test("Find Implementations: IDisposable", "find-implementations", new()
{
    ["typeName"] = "IDisposable"
});

await Test("List Projects", "list-projects", new());

await Test("Expression Type", "expression-type", new()
{
    ["filePath"] = Path.GetFullPath(@"C:\work\dotnet-mcp\src\DotnetMcp\Services\WorkspaceService.cs"),
    ["line"] = 15,
    ["column"] = 5
});

// === Phase 2 tools ===

await Test("Get Source: WorkspaceService", "get-source", new()
{
    ["symbolName"] = "WorkspaceService"
});

await Test("Get Source (method): LoadSolutionAsync", "get-source", new()
{
    ["symbolName"] = "LoadSolutionAsync",
    ["kind"] = "method"
});

await Test("Document Symbols", "get-document-symbols", new()
{
    ["filePath"] = Path.GetFullPath(@"C:\work\dotnet-mcp\src\DotnetMcp\Services\WorkspaceService.cs")
});

await Test("Goto Definition (workspace field usage)", "goto-definition", new()
{
    ["filePath"] = Path.GetFullPath(@"C:\work\dotnet-mcp\src\DotnetMcp\Services\WorkspaceService.cs"),
    ["line"] = 31,
    ["column"] = 9
});

Console.WriteLine("\n\nAll tests completed!");
