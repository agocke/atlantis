using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlantis.Analyzers.CodeFixes;

/// <summary>
/// Code fix for ATL003: Remove unnecessary System.Runtime.InteropServices.JavaScript using.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnnecessaryUsingCodeFix)), Shared]
public sealed class UnnecessaryUsingCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.UnnecessaryJSInteropUsing);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var usingDirective = root.FindNode(diagnosticSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>();
        if (usingDirective == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove unnecessary using",
                createChangedDocument: c => RemoveUsingAsync(context.Document, usingDirective, c),
                equivalenceKey: nameof(UnnecessaryUsingCodeFix)),
            diagnostic);
    }

    private static async Task<Document> RemoveUsingAsync(
        Document document,
        UsingDirectiveSyntax usingDirective,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null) return document;

        var newRoot = root.RemoveNode(usingDirective, SyntaxRemoveOptions.KeepNoTrivia);
        if (newRoot == null) return document;

        return document.WithSyntaxRoot(newRoot);
    }
}
