using Atlantis.Analyzers.CodeFixes;

namespace Atlantis.Analyzers.Tests;

public class JSExportObsoleteAnalyzerTests
{
    [Fact]
    public async Task NoWarning_WhenNoJSExportUsed()
    {
        var source = """
            public class MyClass
            {
                public void MyMethod() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JSExportObsoleteAnalyzer>(source);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Warning_WhenJSExportUsed()
    {
        var source = """
            using System.Runtime.InteropServices.JavaScript;

            public partial class MyClass
            {
                [JSExport]
                public static partial void MyMethod();
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JSExportObsoleteAnalyzer>(source);
        
        Assert.Single(diagnostics);
        Assert.Equal(DiagnosticIds.JSExportObsolete, diagnostics[0].Id);
        Assert.Contains("JSExport", diagnostics[0].GetMessage());
        Assert.Contains("AtlExport", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Warning_WhenJSExportAttributeUsed()
    {
        var source = """
            using System.Runtime.InteropServices.JavaScript;

            public partial class MyClass
            {
                [JSExportAttribute]
                public static partial void MyMethod();
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JSExportObsoleteAnalyzer>(source);
        
        Assert.Single(diagnostics);
        Assert.Equal(DiagnosticIds.JSExportObsolete, diagnostics[0].Id);
    }
}

public class JSExportCodeFixTests
{
    [Fact]
    public async Task CodeFix_ReplacesJSExportWithAtlExport()
    {
        var source = """
            using System.Runtime.InteropServices.JavaScript;

            public partial class MyClass
            {
                [JSExport]
                public static partial void MyMethod();
            }
            """;

        var fixedSource = await AnalyzerTestHelper.ApplyCodeFixAsync<JSExportObsoleteAnalyzer, JSExportCodeFix>(source);
        
        Assert.Contains("[AtlExport]", fixedSource);
        Assert.DoesNotContain("[JSExport]", fixedSource);
    }

    [Fact]
    public async Task CodeFix_ReplacesJSExportAttributeWithAtlExport()
    {
        var source = """
            using System.Runtime.InteropServices.JavaScript;

            public partial class MyClass
            {
                [JSExportAttribute]
                public static partial void MyMethod();
            }
            """;

        var fixedSource = await AnalyzerTestHelper.ApplyCodeFixAsync<JSExportObsoleteAnalyzer, JSExportCodeFix>(source);
        
        Assert.Contains("[AtlExport]", fixedSource);
        Assert.DoesNotContain("[JSExportAttribute]", fixedSource);
    }
}
