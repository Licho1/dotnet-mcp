using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

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

    // File watching
    readonly List<FileSystemWatcher> watchers = [];
    readonly ConcurrentDictionary<string, byte> pendingFileUpdates = new(StringComparer.OrdinalIgnoreCase);
    volatile bool structuralChangeDetected;
    readonly Lock syncLock = new();

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
        StartWatching();

        var projectNames = solution.Projects.Select(p => p.Name).ToList();
        return $"Loaded {solutionPath} with {projectNames.Count} projects: {string.Join(", ", projectNames)}";
    }

    public async Task<string> LoadSlnxAsync(string slnxPath, CancellationToken ct = default)
    {
        slnxPath = Path.GetFullPath(slnxPath);
        if (!File.Exists(slnxPath))
            return $"Solution not found: {slnxPath}";

        if (loadedSolutionPath == slnxPath && solution is not null)
            return $"Solution already loaded: {slnxPath}";

        var dir = Path.GetDirectoryName(slnxPath)!;
        var doc = XDocument.Load(slnxPath);
        var projectPaths = doc.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => p is not null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p!)))
            .Where(File.Exists)
            .ToList();

        if (projectPaths.Count == 0)
            return $"No .csproj projects found in {slnxPath}";

        workspace?.Dispose();
        workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, _) => { };

        foreach (var projectPath in projectPaths)
        {
            // Skip if already loaded as a transitive dependency of a previous project
            if (workspace.CurrentSolution.Projects.Any(p =>
                    string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        }

        solution = workspace.CurrentSolution;
        loadedSolutionPath = slnxPath;
        StartWatching();

        var projectNames = solution.Projects.Select(p => p.Name).ToList();
        return $"Loaded {slnxPath} with {projectNames.Count} projects: {string.Join(", ", projectNames)}";
    }

    public async Task<string> LoadSlnfAsync(string slnfPath, CancellationToken ct = default)
    {
        slnfPath = Path.GetFullPath(slnfPath);
        if (!File.Exists(slnfPath))
            return $"Solution filter not found: {slnfPath}";

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(slnfPath, ct));
        if (!json.RootElement.TryGetProperty("solution", out var solutionElement) ||
            !solutionElement.TryGetProperty("path", out var pathElement))
            return $"Invalid .slnf format: missing solution.path in {slnfPath}";

        var solutionRelPath = pathElement.GetString();
        if (string.IsNullOrEmpty(solutionRelPath))
            return $"Empty solution path in {slnfPath}";

        var dir = Path.GetDirectoryName(slnfPath)!;
        var solutionPath = Path.GetFullPath(Path.Combine(dir, solutionRelPath));

        // Load the referenced solution (could be .sln or .slnx)
        var ext = Path.GetExtension(solutionPath);
        return ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? await LoadSlnxAsync(solutionPath, ct)
            : await LoadSolutionAsync(solutionPath, ct);
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
        StartWatching();

        return $"Loaded project {project.Name} from {projectPath}";
    }

    public async Task<Solution> GetSolutionAsync(CancellationToken ct = default)
    {
        if (solution is null)
            throw new InvalidOperationException("No solution loaded. Use load-solution or load-project first.");

        if (structuralChangeDetected)
        {
            await FullReloadAsync(ct);
        }
        else if (!pendingFileUpdates.IsEmpty)
        {
            lock (syncLock)
                ApplyPendingUpdates();
        }

        return solution!;
    }

    /// <summary>Kept for call sites that don't need freshness (e.g. rename applies its own solution).</summary>
    public Solution GetSolution() =>
        solution ?? throw new InvalidOperationException("No solution loaded. Use load-solution or load-project first.");

    public async Task<IEnumerable<ISymbol>> FindSymbolsAsync(string name, CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct);
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
        var sln = await GetSolutionAsync(ct);
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
        var sln = await GetSolutionAsync(ct);
        return await SymbolFinder.FindReferencesAsync(symbol, sln, ct);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindDerivedTypesAsync(
        INamedTypeSymbol type, CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct);
        return await SymbolFinder.FindDerivedClassesAsync(type, sln, cancellationToken: ct);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindImplementationsAsync(
        INamedTypeSymbol interfaceType, CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct);
        return await SymbolFinder.FindImplementationsAsync(interfaceType, sln, cancellationToken: ct);
    }

    public async Task<SemanticModel?> GetSemanticModelAsync(string filePath, CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct);
        filePath = Path.GetFullPath(filePath);

        var docId = sln.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (docId is null) return null;

        var doc = sln.GetDocument(docId);
        return doc is null ? null : await doc.GetSemanticModelAsync(ct);
    }

    public async Task<SyntaxTree?> GetSyntaxTreeAsync(string filePath, CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct);
        filePath = Path.GetFullPath(filePath);

        var docId = sln.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (docId is null) return null;

        var doc = sln.GetDocument(docId);
        return doc is null ? null : await doc.GetSyntaxTreeAsync(ct);
    }

    // --- File watching ---

    void StartWatching()
    {
        StopWatching();
        if (solution is null) return;

        var dirs = solution.Projects
            .Select(p => Path.GetDirectoryName(p.FilePath))
            .Where(d => d is not null)
            .Select(d => Path.GetFullPath(d!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Also watch solution directory for .sln/.csproj changes
        if (loadedSolutionPath is not null)
        {
            var slnDir = Path.GetDirectoryName(loadedSolutionPath);
            if (slnDir is not null)
                dirs.Add(Path.GetFullPath(slnDir));
        }

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watchers.Add(watcher);
        }
    }

    void StopWatching()
    {
        foreach (var w in watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        watchers.Clear();
        pendingFileUpdates.Clear();
        structuralChangeDetected = false;
    }

    void OnFileChanged(object sender, FileSystemEventArgs e) => ClassifyChange(e.FullPath, e.ChangeType);
    void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // A rename of a .cs file is structural (old doc gone, new doc appeared)
        ClassifyChange(e.OldFullPath, WatcherChangeTypes.Deleted);
        ClassifyChange(e.FullPath, WatcherChangeTypes.Created);
    }

    void ClassifyChange(string fullPath, WatcherChangeTypes changeType)
    {
        // Ignore bin/obj/.vs directories
        if (fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return;

        var ext = Path.GetExtension(fullPath);

        // Structural changes: project/solution files, or files added/deleted
        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase) ||
            changeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted)
        {
            structuralChangeDetected = true;
            return;
        }

        // Content change to a .cs file — can be applied incrementally
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) && changeType == WatcherChangeTypes.Changed)
            pendingFileUpdates[fullPath] = 0;
    }

    void ApplyPendingUpdates()
    {
        if (solution is null) return;

        var files = pendingFileUpdates.Keys.ToList();
        pendingFileUpdates.Clear();

        foreach (var filePath in files)
        {
            var docIds = solution.GetDocumentIdsWithFilePath(filePath);
            if (docIds.IsEmpty) continue;

            try
            {
                var text = SourceText.From(File.ReadAllText(filePath));
                foreach (var docId in docIds)
                    solution = solution.WithDocumentText(docId, text);
            }
            catch (IOException)
            {
                // File may be locked mid-write, will catch it next time
            }
        }
    }

    async Task FullReloadAsync(CancellationToken ct)
    {
        if (loadedSolutionPath is null) return;

        var ext = Path.GetExtension(loadedSolutionPath);
        var path = loadedSolutionPath;

        // Reset so Load methods don't short-circuit with "already loaded"
        loadedSolutionPath = null;
        solution = null;

        switch (ext.ToLowerInvariant())
        {
            case ".sln": await LoadSolutionAsync(path, ct); break;
            case ".slnx": await LoadSlnxAsync(path, ct); break;
            case ".slnf": await LoadSlnfAsync(path, ct); break;
            default: await LoadProjectAsync(path, ct); break;
        }
    }

    public void Dispose()
    {
        StopWatching();
        workspace?.Dispose();
    }
}
