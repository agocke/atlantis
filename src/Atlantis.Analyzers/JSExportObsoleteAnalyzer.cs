using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlantis.Analyzers;

/// <summary>
/// ATL001: Detects usage of [JSExport] and suggests [AtlExport] instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JSExportObsoleteAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.JSExportObsolete,
        title: "JSExport is obsolete",
        messageFormat: "[JSExport] is deprecated. Use [AtlExport] instead.",
        category: "Atlantis",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The [JSExport] attribute from System.Runtime.InteropServices.JavaScript is not used by Atlantis. Use [AtlExport] instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => 
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        var name = attribute.Name.ToString();

        // Check for JSExport or JSExportAttribute
        if (name is not ("JSExport" or "JSExportAttribute"))
            return;

        // Verify it's the actual JSExport from System.Runtime.InteropServices.JavaScript
        var symbolInfo = context.SemanticModel.GetSymbolInfo(attribute);
        if (symbolInfo.Symbol is IMethodSymbol constructor)
        {
            var containingType = constructor.ContainingType;
            var ns = containingType.ContainingNamespace?.ToDisplayString();

            if (ns == "System.Runtime.InteropServices.JavaScript" ||
                containingType.Name == "JSExportAttribute")
            {
                var diagnostic = Diagnostic.Create(Rule, attribute.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
