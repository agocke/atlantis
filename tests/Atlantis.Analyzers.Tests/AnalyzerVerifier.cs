using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Atlantis.Analyzers.Tests;

/// <summary>
/// Test utilities for Atlantis analyzer tests.
/// </summary>
public static class AnalyzerTestHelper
{
    private static readonly MetadataReference[] DefaultReferences =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.JavaScript.JSExportAttribute).Assembly.Location)
    ];

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new TAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers([analyzer]);

        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }

    public static async Task<string> ApplyCodeFixAsync<TAnalyzer, TCodeFix>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : Microsoft.CodeAnalysis.CodeFixes.CodeFixProvider, new()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new TAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers([analyzer]);
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        if (diagnostics.IsEmpty)
            return source;

        var document = CreateDocument(source);
        var codeFix = new TCodeFix();
        
        Microsoft.CodeAnalysis.CodeActions.CodeAction? action = null;
        var context = new Microsoft.CodeAnalysis.CodeFixes.CodeFixContext(
            document,
            diagnostics[0],
            (a, _) => action = a,
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context);

        if (action == null)
            return source;

        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<Microsoft.CodeAnalysis.CodeActions.ApplyChangesOperation>().First().ChangedSolution;
        var changedDocument = changedSolution.GetDocument(document.Id)!;
        var changedText = await changedDocument.GetTextAsync();

        return changedText.ToString();
    }

    private static Document CreateDocument(string source)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, DefaultReferences)
            .AddDocument(documentId, "Test.cs", SourceText.From(source));

        return solution.GetDocument(documentId)!;
    }
}

