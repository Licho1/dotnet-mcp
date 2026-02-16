using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotnetMcp.Services;

public class WorkspaceService : IDisposable
{
    static WorkspaceService()
    {
        MSBuildLocator.RegisterDefaults();
    }

    MSBuildWorkspace? workspace;
    Solution? solution;
    string? loadedSolutionPath;

    public bool IsLoaded => solution is not null;
    public string? LoadedPath => loadedSolutionPath;

    public async Task<string> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(solutionPath))
            return $"Solution not found: {solutionPath}";

        if (loadedSolutionPath == solutionPath && solution is not null)
            return $"Solution already loaded: {solutionPath}";

        workspace?.Dispose();
        workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(_ => { }); // suppress diagnostics to stderr

        solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        loadedSolutionPath = solutionPath;

        var projectNames = solution.Projects.Select(p => p.Name).ToList();
        return $"Loaded {solutionPath} with {projectNames.Count} projects: {string.Join(", ", projectNames)}";
    }

    public async Task<string> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
            return $"Project not found: {projectPath}";

        workspace?.Dispose();
        workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(_ => { });

        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        solution = project.Solution;
        loadedSolutionPath = projectPath;

        return $"Loaded project {project.Name} from {projectPath}";
    }

    public Solution GetSolution() =>
        solution ?? throw new InvalidOperationException("No solution loaded. Use load-solution or load-project first.");

    public async Task<IEnumerable<ISymbol>> FindSymbolsAsync(string name, CancellationToken ct = default)
    {
        var sln = GetSolution();
        var results = new List<ISymbol>();

        foreach (var project in sln.Projects)
        {
            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project, name, ignoreCase: false, cancellationToken: ct);
            results.AddRange(symbols);
        }

        return results;
    }

    public async Task<IEnumerable<ISymbol>> FindSymbolsWithFilterAsync(
        string name, Func<string, bool> filter, CancellationToken ct = default)
    {
        var sln = GetSolution();
        var results = new List<ISymbol>();

        foreach (var project in sln.Projects)
        {
            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project, filter, cancellationToken: ct);
            results.AddRange(symbols.Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        }

        return results;
    }

    public async Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(ISymbol symbol, CancellationToken ct = default)
    {
        var sln = GetSolution();
        return await SymbolFinder.FindReferencesAsync(symbol, sln, ct);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindDerivedTypesAsync(
        INamedTypeSymbol type, CancellationToken ct = default)
    {
        var sln = GetSolution();
        return await SymbolFinder.FindDerivedClassesAsync(type, sln, cancellationToken: ct);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindImplementationsAsync(
        INamedTypeSymbol interfaceType, CancellationToken ct = default)
    {
        var sln = GetSolution();
        return await SymbolFinder.FindImplementationsAsync(interfaceType, sln, cancellationToken: ct);
    }

    public async Task<SemanticModel?> GetSemanticModelAsync(string filePath, CancellationToken ct = default)
    {
        var sln = GetSolution();
        filePath = Path.GetFullPath(filePath);

        var docId = sln.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (docId is null) return null;

        var doc = sln.GetDocument(docId);
        return doc is null ? null : await doc.GetSemanticModelAsync(ct);
    }

    public async Task<SyntaxTree?> GetSyntaxTreeAsync(string filePath, CancellationToken ct = default)
    {
        var sln = GetSolution();
        filePath = Path.GetFullPath(filePath);

        var docId = sln.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (docId is null) return null;

        var doc = sln.GetDocument(docId);
        return doc is null ? null : await doc.GetSyntaxTreeAsync(ct);
    }

    public void Dispose()
    {
        workspace?.Dispose();
    }
}
