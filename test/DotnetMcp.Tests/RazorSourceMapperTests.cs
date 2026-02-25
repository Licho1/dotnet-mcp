using DotnetMcp.Services;
using Xunit;

namespace DotnetMcp.Tests;

public class RazorSourceMapperTests : IDisposable
{
    readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("DotnetMcpTests_");

    public void Dispose() => _tmpDir.Delete(recursive: true);

    // Creates a fake project: projDir/obj/Index.cshtml.g.cs + projDir/Index.cshtml
    (string cshtmlPath, string gcsPath) CreateTempProject(string gcsContent)
    {
        var objDir = Path.Combine(_tmpDir.FullName, "obj");
        Directory.CreateDirectory(objDir);

        var cshtmlPath = Path.Combine(_tmpDir.FullName, "Index.cshtml");
        File.WriteAllText(cshtmlPath, "<h1>Hello</h1>");

        var gcsPath = Path.Combine(objDir, "Index.cshtml.g.cs");
        File.WriteAllText(gcsPath, gcsContent);

        return (cshtmlPath, gcsPath);
    }

    [Fact]
    public void TryMapReverse_OldStyle_MapsCorrectly()
    {
        // #line N "file.cshtml" at generated line 5 → cshtml line 10
        // Lines: 1=comment, 2=void, 3={, 4=var x, 5=#line directive, 6=mapped code, 7=#line default, 8=}
        var cshtml = Path.Combine(_tmpDir.FullName, "Index.cshtml");
        var (cshtmlPath, gcsPath) = CreateTempProject(
            "// header\n" +
            "void Build()\n" +
            "{\n" +
            "    var x = 1;\n" +
            $"#line 10 \"{cshtml}\"\n" +
            "    var model = Model.Name;\n" +
            "#line default\n" +
            "}\n");

        var mapper = new RazorSourceMapper();
        mapper.TryMap(gcsPath, 6); // populates cache

        var result = mapper.TryMapReverse(cshtmlPath, 10);

        Assert.NotNull(result);
        Assert.Equal(gcsPath, result.Value.gcsPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(6, result.Value.gcsLine); // #line at line 5, next line = 6
    }

    [Fact]
    public void TryMapReverse_NewStyle_MapsCorrectly()
    {
        // #line (row,col)-(row,col) N "file.cshtml" at generated line 4
        var cshtml = Path.Combine(_tmpDir.FullName, "Index.cshtml");
        var (cshtmlPath, gcsPath) = CreateTempProject(
            "// header\n" +
            "void Build()\n" +
            "{\n" +
            $"#line (15,5)-(15,20) 16 \"{cshtml}\"\n" +
            "    var model = Model.Name;\n" +
            "#line default\n" +
            "}\n");

        var mapper = new RazorSourceMapper();
        mapper.TryMap(gcsPath, 5);

        var result = mapper.TryMapReverse(cshtmlPath, 15);

        Assert.NotNull(result);
        Assert.Equal(gcsPath, result.Value.gcsPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, result.Value.gcsLine); // #line at line 4, next = 5
        Assert.Equal(5, result.Value.gcsCol);  // col from directive
    }

    [Fact]
    public void TryMapReverse_LineOffset_MapsCorrectly()
    {
        // #line directive at gen line 4 → cshtml line 20
        // cshtml line 22 = cshtml 20 + 2 → gen line 4+1+2 = 7
        var cshtml = Path.Combine(_tmpDir.FullName, "Index.cshtml");
        var (cshtmlPath, gcsPath) = CreateTempProject(
            "// line 1\n" +
            "void Build()\n" +
            "{\n" +
            $"#line 20 \"{cshtml}\"\n" +
            "    line_20();\n" +
            "    line_21();\n" +
            "    line_22();\n" +
            "#line default\n" +
            "}\n");

        var mapper = new RazorSourceMapper();
        mapper.TryMap(gcsPath, 5);

        var result = mapper.TryMapReverse(cshtmlPath, 22);

        Assert.NotNull(result);
        Assert.Equal(7, result.Value.gcsLine);
    }

    [Fact]
    public void TryMapReverse_NoMapping_ReturnsNull()
    {
        var (cshtmlPath, gcsPath) = CreateTempProject("// no line directives\nvoid Build() { }\n");
        var mapper = new RazorSourceMapper();
        mapper.TryMap(gcsPath, 1);

        var result = mapper.TryMapReverse(cshtmlPath, 5);

        Assert.Null(result);
    }

    [Fact]
    public void TryMapReverse_RoundTrip_ForwardThenReverse()
    {
        // Forward: generated line 5 → cshtml line 8
        // Reverse: cshtml line 8 → generated line 5
        var cshtml = Path.Combine(_tmpDir.FullName, "Index.cshtml");
        var (cshtmlPath, gcsPath) = CreateTempProject(
            "// header\n" +
            "void Build()\n" +
            "{\n" +
            $"#line 8 \"{cshtml}\"\n" +
            "    var x = Foo();\n" +
            "#line default\n" +
            "}\n");

        var mapper = new RazorSourceMapper();

        var forward = mapper.TryMap(gcsPath, 5);
        Assert.NotNull(forward);
        Assert.Equal(8, forward.Value.cshtmlLine);

        var reverse = mapper.TryMapReverse(cshtmlPath, 8);
        Assert.NotNull(reverse);
        Assert.Equal(5, reverse.Value.gcsLine);
    }
}
