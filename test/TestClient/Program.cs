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
var testType = isSelf ? "WorkspaceService" : (args.Length > 1 ? args[1] : "CsvTable");
var testMethod = isSelf ? "LoadSolutionAsync" : (args.Length > 2 ? args[2] : "ParseCsvLine");

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

// === Razor (.cshtml) Reference Test ===
// When testing against a Razor project, verify that find-references returns .cshtml locations.

if (!isSelf)
{
    Console.WriteLine("\n=== Razor (.cshtml) Reference Check (by symbol name) ===");
    var result = await client.CallToolAsync("find-references", new Dictionary<string, object?>
    {
        ["symbolName"] = testType
    });
    var text = GetText(result);
    var cshtmlRefs = text.Split('\n').Where(l => l.Contains(".cshtml")).ToList();
    if (cshtmlRefs.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Found {cshtmlRefs.Count} .cshtml reference(s):");
        foreach (var r in cshtmlRefs)
            Console.WriteLine(r);
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠ No .cshtml references found (symbol may not be used in Razor views)");
        Console.WriteLine(text);
    }
    Console.ResetColor();

    // === NEW: find-references from a .cshtml file:line:col ===
    // Find any .cshtml reference from the previous result and use it to test the reverse lookup
    var cshtmlRef = cshtmlRefs.FirstOrDefault(l => l.Contains(".cshtml:"));
    if (cshtmlRef is not null)
    {
        // Parse "  /path/to/File.cshtml:LINE   [Razor]" → path + line
        var trimmed = cshtmlRef.Trim();
        var parts = trimmed.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[^1].Split(' ')[0].Split('\t')[0].Trim(), out var cshtmlLine))
        {
            // Reconstruct path (handle Windows drive letter like C:)
            var cshtmlPath = string.Join(":", parts[..^1]).Trim();
            Console.WriteLine($"\n=== find-references from cshtml file:line ({cshtmlPath}:{cshtmlLine}) ===");
            await Test("find-references (from .cshtml file:line)", "find-references", new()
            {
                ["filePath"] = cshtmlPath,
                ["line"] = cshtmlLine,
                ["column"] = 1
            });

            Console.WriteLine($"\n=== get-source from cshtml file:line ({cshtmlPath}:{cshtmlLine}) ===");
            await Test("get-source (from .cshtml file:line)", "get-source", new()
            {
                ["filePath"] = cshtmlPath,
                ["line"] = cshtmlLine,
                ["column"] = 1
            });
        }
    }
}

Console.WriteLine("\n\nAll tests completed!");
