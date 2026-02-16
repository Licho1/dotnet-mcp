using System.ComponentModel;
using DotnetMcp.Services;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool(Name = "load-solution"), Description("Load a .sln solution file for analysis. Must be called before using other tools.")]
    public static async Task<string> LoadSolution(
        WorkspaceService workspace,
        [Description("Full path to the .sln file")] string solutionPath,
        CancellationToken ct) =>
        await workspace.LoadSolutionAsync(solutionPath, ct);

    [McpServerTool(Name = "load-project"), Description("Load a single .csproj project file for analysis. Alternative to load-solution.")]
    public static async Task<string> LoadProject(
        WorkspaceService workspace,
        [Description("Full path to the .csproj file")] string projectPath,
        CancellationToken ct) =>
        await workspace.LoadProjectAsync(projectPath, ct);

    [McpServerTool(Name = "list-projects"), Description("List all projects in the loaded solution.")]
    public static string ListProjects(WorkspaceService workspace)
    {
        var sln = workspace.GetSolution();
        var lines = sln.Projects
            .OrderBy(p => p.Name)
            .Select(p => $"- {p.Name} ({p.FilePath ?? "unknown path"}) [{p.Documents.Count()} files]");
        return string.Join('\n', lines);
    }
}
