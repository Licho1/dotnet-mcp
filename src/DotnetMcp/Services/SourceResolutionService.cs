using System.IO.Compression;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using DotnetMcp.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;

namespace DotnetMcp.Services;

public record SourceResult(string Source, string FilePath, string ResolutionMethod, bool IsOriginalSource);

public class SourceResolutionService(WorkspaceService workspace)
{
    static readonly HttpClient Http = new();
    static readonly Guid SourceLinkGuid = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    static readonly Guid EmbeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    public async Task<SourceResult?> ResolveAsync(ISymbol symbol, CancellationToken ct = default)
    {
        // Tier 1: Local source (syntax tree)
        if (symbol.DeclaringSyntaxReferences.Length > 0)
        {
            var syntaxRef = symbol.DeclaringSyntaxReferences[0];
            var node = await syntaxRef.GetSyntaxAsync(ct);

            // Walk up to the full declaration node
            var declaration = node.AncestorsAndSelf().FirstOrDefault(n =>
                n is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax
                or FieldDeclarationSyntax or EnumDeclarationSyntax or InterfaceDeclarationSyntax
                or ConstructorDeclarationSyntax or EventDeclarationSyntax or DelegateDeclarationSyntax
                or RecordDeclarationSyntax) ?? node;

            var lineSpan = syntaxRef.SyntaxTree.GetLineSpan(declaration.Span);
            return new(
                declaration.ToFullString().Trim(),
                $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}",
                "Local Source",
                true);
        }

        var assemblyPath = FindAssemblyPath(symbol);
        if (assemblyPath is null) return null;

        // Tier 2: SourceLink (download original source from PDB metadata)
        var result = await TrySourceLinkAsync(symbol, assemblyPath, ct);
        if (result is not null) return result;

        // Tier 3: Embedded source in PDB
        result = TryEmbeddedSource(symbol, assemblyPath);
        if (result is not null) return result;

        // Tier 4: Decompilation fallback
        return TryDecompile(symbol, assemblyPath);
    }

    string? FindAssemblyPath(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly;
        if (assembly is null) return null;

        var sln = workspace.GetSolution();
        foreach (var project in sln.Projects)
        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (reference.FilePath is not null &&
                Path.GetFileNameWithoutExtension(reference.FilePath) == assembly.Name)
                return reference.FilePath;
        }

        return null;
    }

    async Task<SourceResult?> TrySourceLinkAsync(ISymbol symbol, string assemblyPath, CancellationToken ct)
    {
        try
        {
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath)) return null;

            using var pdbStream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();

            // Find SourceLink JSON in custom debug info
            string? sourceLinkJson = null;
            foreach (var handle in reader.CustomDebugInformation)
            {
                var info = reader.GetCustomDebugInformation(handle);
                if (reader.GetGuid(info.Kind) == SourceLinkGuid)
                {
                    var blob = reader.GetBlobReader(info.Value);
                    sourceLinkJson = Encoding.UTF8.GetString(blob.ReadBytes(blob.Length));
                    break;
                }
            }

            if (sourceLinkJson is null) return null;

            // Parse SourceLink JSON: { "documents": { "pattern*": "url*" } }
            using var doc = JsonDocument.Parse(sourceLinkJson);
            var documents = doc.RootElement.GetProperty("documents");

            // Get the symbol's document path from PDB
            var symbolPath = GetSymbolDocumentPath(symbol, reader);
            if (symbolPath is null) return null;

            symbolPath = symbolPath.Replace('\\', '/');

            // Match against SourceLink patterns
            foreach (var prop in documents.EnumerateObject())
            {
                var pattern = prop.Name;
                var url = prop.Value.GetString();
                if (url is null) continue;

                if (MatchWildcard(symbolPath, pattern, out var wildcard))
                {
                    var sourceUrl = url.Replace("*", wildcard);
                    var source = await Http.GetStringAsync(sourceUrl, ct);
                    return new(source, sourceUrl, "Source Link", true);
                }
            }
        }
        catch
        {
            // SourceLink is best-effort, fall through to next tier
        }

        return null;
    }

    static string? GetSymbolDocumentPath(ISymbol symbol, MetadataReader reader)
    {
        // Try to find the document path from PDB documents matching the symbol's type name
        var targetFile = (symbol.ContainingType?.Name ?? symbol.Name) + ".cs";

        foreach (var docHandle in reader.Documents)
        {
            var doc = reader.GetDocument(docHandle);
            var name = reader.GetString(doc.Name);
            if (name.EndsWith(targetFile, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // If no match by type name, try the symbol name itself
        targetFile = symbol.Name + ".cs";
        foreach (var docHandle in reader.Documents)
        {
            var doc = reader.GetDocument(docHandle);
            var name = reader.GetString(doc.Name);
            if (name.EndsWith(targetFile, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    static bool MatchWildcard(string path, string pattern, out string wildcard)
    {
        wildcard = "";
        var starIdx = pattern.IndexOf('*');
        if (starIdx < 0)
        {
            return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var prefix = pattern[..starIdx];
        var suffix = pattern[(starIdx + 1)..];

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            (suffix.Length > 0 && !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return false;

        wildcard = path[prefix.Length..^(suffix.Length > 0 ? suffix.Length : 0)];
        return true;
    }

    SourceResult? TryEmbeddedSource(ISymbol symbol, string assemblyPath)
    {
        try
        {
            var sources = ReadEmbeddedSources(assemblyPath);

            // If nothing in assembly, try standalone PDB
            if (sources.Count == 0)
            {
                var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                if (File.Exists(pdbPath))
                    sources = ReadEmbeddedSourcesFromPdb(pdbPath);
            }

            if (sources.Count == 0) return null;

            // Match by containing type name
            var targetFile = (symbol.ContainingType?.Name ?? symbol.Name) + ".cs";
            var match = sources.FirstOrDefault(kv =>
                Path.GetFileName(kv.Key).Equals(targetFile, StringComparison.OrdinalIgnoreCase));

            // Fallback: match by symbol name
            if (match.Value is null)
            {
                targetFile = symbol.Name + ".cs";
                match = sources.FirstOrDefault(kv =>
                    Path.GetFileName(kv.Key).Equals(targetFile, StringComparison.OrdinalIgnoreCase));
            }

            // Last resort: if single file, use it
            if (match.Value is null && sources.Count == 1)
                match = sources.First();

            if (match.Value is null) return null;

            return new(match.Value, match.Key, "Embedded Source", true);
        }
        catch
        {
            return null;
        }
    }

    static Dictionary<string, string> ReadEmbeddedSources(string assemblyPath)
    {
        using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var peReader = new PEReader(fs);

        var debugDirs = peReader.ReadDebugDirectory();
        var embeddedPdb = debugDirs.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        if (embeddedPdb.DataSize == 0) return [];

        using var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
        return ExtractEmbeddedSources(provider.GetMetadataReader());
    }

    static Dictionary<string, string> ReadEmbeddedSourcesFromPdb(string pdbPath)
    {
        using var fs = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        return ExtractEmbeddedSources(provider.GetMetadataReader());
    }

    static Dictionary<string, string> ExtractEmbeddedSources(MetadataReader reader)
    {
        var results = new Dictionary<string, string>();

        foreach (var handle in reader.CustomDebugInformation)
        {
            var info = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(info.Kind) != EmbeddedSourceGuid) continue;
            if (info.Parent.Kind != HandleKind.Document) continue;

            var docHandle = (DocumentHandle)info.Parent;
            var doc = reader.GetDocument(docHandle);
            var fileName = reader.GetString(doc.Name);

            var blob = reader.GetBlobReader(info.Value);
            var format = blob.ReadInt32();
            var contentBytes = blob.ReadBytes(blob.Length - blob.Offset);

            string sourceText;
            if (format == 0)
            {
                sourceText = Encoding.UTF8.GetString(contentBytes);
            }
            else if (format > 0)
            {
                using var compressed = new MemoryStream(contentBytes);
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();
                deflate.CopyTo(decompressed);
                sourceText = Encoding.UTF8.GetString(decompressed.ToArray());
            }
            else continue;

            results[fileName] = sourceText;
        }

        return results;
    }

    SourceResult? TryDecompile(ISymbol symbol, string assemblyPath)
    {
        try
        {
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(assemblyPath, settings);

            if (symbol is INamedTypeSymbol namedType)
            {
                var fullName = new FullTypeName(namedType.GetMetadataName());
                var source = decompiler.DecompileTypeAsString(fullName);
                return new(source, $"{assemblyPath} (decompiled)", "Decompilation", false);
            }

            if (symbol.ContainingType is not null)
            {
                var parentName = new FullTypeName(symbol.ContainingType.GetMetadataName());
                var typeDef = decompiler.TypeSystem.MainModule.GetTypeDefinition(parentName.TopLevelTypeName);
                if (typeDef is not null)
                {
                    var member = typeDef.Members.FirstOrDefault(m => m.Name == symbol.Name);
                    if (member is not null)
                    {
                        var source = decompiler.DecompileAsString(member.MetadataToken);
                        return new(source, $"{assemblyPath} (decompiled)", "Decompilation", false);
                    }
                }

                // Fallback to whole type
                var typeSource = decompiler.DecompileTypeAsString(parentName);
                return new(typeSource, $"{assemblyPath} (decompiled)", "Decompilation", false);
            }
        }
        catch
        {
            // Decompilation can fail for various reasons
        }

        return null;
    }
}
