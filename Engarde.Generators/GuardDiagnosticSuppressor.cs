using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Engarde.Generators;

/// <summary>
/// Suppresses attribute conventions that do not apply to guards, so a custom guard needs no pragmas
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GuardDiagnosticSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor AttributeSuffix = new(
        "EGS1001", "S3376", "Guards are named after the feature or access they protect; the Attribute suffix is optional");

    private static readonly SuppressionDescriptor AttributeUsage = new(
        "EGS1002", "S3993", "Guards inherit the AttributeUsage of EngardeAttribute");

    private static readonly SuppressionDescriptor UnsealedTemplate = new(
        "EGS1003", "S4060", "Feature guard templates and guards forwarding feature names are meant to be derived from");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } =
        ImmutableArray.Create(AttributeSuffix, AttributeUsage, UnsealedTemplate);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var descriptor = diagnostic.Id == AttributeSuffix.SuppressedDiagnosticId ? AttributeSuffix
                : diagnostic.Id == AttributeUsage.SuppressedDiagnosticId ? AttributeUsage
                : diagnostic.Id == UnsealedTemplate.SuppressedDiagnosticId ? UnsealedTemplate : null;
            if (descriptor is null || diagnostic.Location.SourceTree is not { } tree)
                continue;

            var declaration = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan)
                .AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var model = context.GetSemanticModel(tree);
            if (declaration is not null &&
                model.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol type &&
                GuardGenerator.IsGuard(type) &&
                (descriptor != UnsealedTemplate || IsDerivable(type, model.Compilation)))
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
        }
    }

    // A [GenerateFeatureGuards] template must stay unsealed; a guard whose constructors all forward feature names
    // is a supported base for other guards. Other guards keep the rule.
    private static bool IsDerivable(INamedTypeSymbol type, Compilation compilation) =>
        type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Engarde.GenerateFeatureGuardsAttribute") ||
        type.InstanceConstructors.All(constructor => GuardGenerator.ConstructorForwardsFeatures(constructor, compilation));
}
