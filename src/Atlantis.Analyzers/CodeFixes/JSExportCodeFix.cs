using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlantis.Analyzers.CodeFixes;

/// <summary>
/// Code fix for ATL001: Replace [JSExport] with [AtlExport].
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JSExportCodeFix)), Shared]
public sealed class JSExportCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.JSExportObsolete);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var attribute = root.FindNode(diagnosticSpan).FirstAncestorOrSelf<AttributeSyntax>();
        if (attribute == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Replace with [AtlExport]",
                createChangedDocument: c => ReplaceWithAtlExportAsync(context.Document, attribute, c),
                equivalenceKey: nameof(JSExportCodeFix)),
            diagnostic);
    }

    private static async Task<Document> ReplaceWithAtlExportAsync(
        Document document,
        AttributeSyntax attribute,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null) return document;

        // Create the new AtlExport attribute
        var newName = SyntaxFactory.IdentifierName("AtlExport");
        var newAttribute = attribute.WithName(newName);

        var newRoot = root.ReplaceNode(attribute, newAttribute);
        return document.WithSyntaxRoot(newRoot);
    }
}
