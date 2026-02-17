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

// Determine what to load: pass a path as first arg, or default to self
var loadPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : serverPath;
var isSelf = loadPath == serverPath;

// === Load ===

await Test("Load", "load", new() { ["path"] = loadPath });

// Use different symbol names depending on what we loaded
var testType = isSelf ? "WorkspaceService" : "CsvTable";
var testMethod = isSelf ? "LoadSolutionAsync" : "ParseCsvLine";

// === Type Hierarchy ===

await Test($"Type Hierarchy: {testType}", "type-hierarchy", new()
{
    ["typeName"] = testType
});

// === Find References ===

await Test($"Find References: {testType}", "find-references", new()
{
    ["symbolName"] = testType
});

// === Find Implementations ===

var testInterface = isSelf ? "IDisposable" : "ICustomHeader";
await Test($"Find Implementations: {testInterface}", "find-implementations", new()
{
    ["typeName"] = testInterface
});

// === Get Source (by name) ===

await Test($"Get Source (by name): {testType}", "get-source", new()
{
    ["symbolName"] = testType
});

await Test($"Get Source (by name, method): {testMethod}", "get-source", new()
{
    ["symbolName"] = testMethod,
    ["kind"] = "method"
});

// === Find Callers ===

await Test($"Find Callers: {testMethod}", "find-callers", new()
{
    ["methodName"] = testMethod
});

// === File Watching Test ===

if (isSelf)
{
    Console.WriteLine("\n=== File Watch: incremental update test ===");

    // Create a temp .cs file in the project directory
    var projectDir = Path.GetDirectoryName(serverPath)!;
    var tempFile = Path.Combine(projectDir, "TestWatchTarget.cs");

    try
    {
        // Write a class, wait for watcher to pick it up, then query
        File.WriteAllText(tempFile, "namespace DotnetMcp; public class TestWatchTarget { public void OriginalMethod() { } }");
        Console.WriteLine("  Wrote TestWatchTarget.cs with OriginalMethod");
        await Task.Delay(500); // let watcher fire

        // This will trigger a structural reload (new file created)
        await Test("Find TestWatchTarget (after create)", "get-source", new() { ["symbolName"] = "TestWatchTarget" });

        // Now modify the file content (incremental update path)
        File.WriteAllText(tempFile, "namespace DotnetMcp; public class TestWatchTarget { public void ModifiedMethod() { } }");
        Console.WriteLine("\n  Modified TestWatchTarget.cs: OriginalMethod → ModifiedMethod");
        await Task.Delay(500);

        await Test("Find TestWatchTarget (after modify)", "get-source", new() { ["symbolName"] = "TestWatchTarget" });

        // Rapid saves test
        Console.WriteLine("\n  Rapid-saving TestWatchTarget.cs 5 times...");
        for (var i = 0; i < 5; i++)
            File.WriteAllText(tempFile, $"namespace DotnetMcp; public class TestWatchTarget {{ public void Version{i}() {{ }} }}");
        await Task.Delay(500);

        await Test("Find TestWatchTarget (after rapid saves)", "get-source", new() { ["symbolName"] = "TestWatchTarget" });
    }
    finally
    {
        if (File.Exists(tempFile))
            File.Delete(tempFile);
        Console.WriteLine("\n  Cleaned up TestWatchTarget.cs");
    }
}

Console.WriteLine("\n\nAll tests completed!");
