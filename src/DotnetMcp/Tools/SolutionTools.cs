using System.ComponentModel;
using DotnetMcp.Services;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool(Name = "load"), Description(
        "Load a .sln solution or .csproj project file for analysis. Must be called before using other tools.")]
    public static async Task<string> Load(
        WorkspaceService workspace,
        [Description("Full path to a .sln or .csproj file")] string path,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            ? await workspace.LoadSolutionAsync(path, ct)
            : await workspace.LoadProjectAsync(path, ct);
    }
}
