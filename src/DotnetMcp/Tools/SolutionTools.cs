using System.ComponentModel;
using DotnetMcp.Services;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool(Name = "load"), Description(
        "Load a .sln/.slnx/.slnf solution or .csproj project file for analysis. Must be called before using other tools.")]
    public static async Task<string> Load(
        WorkspaceService workspace,
        [Description("Full path to a .sln, .slnx, .slnf, or .csproj file")] string path,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".sln" => await workspace.LoadSolutionAsync(path, ct),
            ".slnx" => await workspace.LoadSlnxAsync(path, ct),
            ".slnf" => await workspace.LoadSlnfAsync(path, ct),
            _ => await workspace.LoadProjectAsync(path, ct)
        };
    }
}
