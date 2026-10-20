using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Engarde.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GuardUsageAnalyzer : DiagnosticAnalyzer
{
    private const string GuardType = "Engarde.EngardeAttribute";
    private const string FallbackMarker = "Engarde.EngardeFallbackConstructorAttribute";
    private const string ValueMarker = "Engarde.EngardeValueConstructorAttribute";

    private static readonly DiagnosticDescriptor InvalidTarget = new(
        "EG1004", "Guard constructor is used on an incompatible target",
        "{0}", "Engarde", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InvalidFallbackType = new(
        "EG1005", "Invalid fallback type",
        "{0}", "Engarde", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InvalidFallbackMethod = new(
        "EG1006", "Invalid fallback method",
        "{0}", "Engarde", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InvalidGuardedValue = new(
        "EG1007", "Invalid guarded value",
        "{0}", "Engarde", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidTarget, InvalidFallbackType, InvalidFallbackMethod, InvalidGuardedValue);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze,
            SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field, SymbolKind.Parameter);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        foreach (var attribute in context.Symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeType || !IsGuard(attributeType) ||
                attribute.AttributeConstructor is not { } constructor)
                continue;

            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                ?? context.Symbol.Locations.FirstOrDefault();
            if (HasMarker(constructor, ValueMarker))
                AnalyzeValue(context, attribute, constructor, location);
            if (FallbackKind(constructor) is { } kind)
                AnalyzeFallback(context, attribute, kind, location);
        }
    }

    private static void AnalyzeValue(SymbolAnalysisContext context, AttributeData attribute,
        IMethodSymbol constructor, Location? location)
    {
        ITypeSymbol? targetType = context.Symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null
        };
        if (targetType is null)
        {
            Report(context, InvalidTarget, location,
                "A value-selective guard can only be applied to a field, property, or parameter.");
            return;
        }

        var valueIndex = constructor.Parameters.IndexOf(parameter =>
            parameter.Name.TrimStart('_') == "guardedValue");
        if (valueIndex < 0 || valueIndex >= attribute.ConstructorArguments.Length)
            return;
        var value = attribute.ConstructorArguments[valueIndex];
        if (IsDefault(value))
        {
            Report(context, InvalidGuardedValue, location,
                "A guarded value cannot be null, empty, or the type's default value because Engarde skips default values.");
            return;
        }
        if (value.Type is { } valueType && !((CSharpCompilation)context.Compilation)
                .ClassifyConversion(valueType, targetType).IsImplicit)
            Report(context, InvalidGuardedValue, location,
                $"Guarded value of type '{valueType.ToDisplayString()}' is not assignable to '{targetType.ToDisplayString()}'.");
    }

    private static void AnalyzeFallback(SymbolAnalysisContext context, AttributeData attribute,
        int kind, Location? location)
    {
        if (kind == 0)
        {
            if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } guarded)
            {
                Report(context, InvalidTarget, location,
                    "A type fallback can only be applied to a class or interface.");
                return;
            }
            var fallback = attribute.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
            if (fallback is null || fallback.TypeKind != TypeKind.Class || fallback.IsAbstract ||
                fallback.IsUnboundGenericType || !((CSharpCompilation)context.Compilation)
                    .ClassifyConversion(fallback, guarded).IsImplicit)
                Report(context, InvalidFallbackType, location,
                    $"Fallback type must be a concrete class assignable to '{guarded.ToDisplayString()}'.");
            return;
        }

        if (context.Symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } guardedMethod)
        {
            Report(context, InvalidTarget, location,
                "A method fallback can only be applied to an ordinary method.");
            return;
        }
        if (attribute.ConstructorArguments.Length < 2 ||
            attribute.ConstructorArguments[0].Value is not string name ||
            attribute.ConstructorArguments[1].Value is not INamedTypeSymbol fallbackType)
            return;

        var candidate = fallbackType.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(method =>
            (!guardedMethod.IsStatic || method.IsStatic) && SameSignature(method, guardedMethod));
        if (candidate is null || SymbolEqualityComparer.Default.Equals(candidate, guardedMethod))
            Report(context, InvalidFallbackMethod, location,
                $"Fallback method '{fallbackType.ToDisplayString()}.{name}' must differ from the guarded method and have a compatible static/instance form, generic arity, parameters, and return type.");
    }

    private static bool SameSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (left.Arity != right.Arity || left.Parameters.Length != right.Parameters.Length ||
            !SameType(left.ReturnType, right.ReturnType))
            return false;
        for (var index = 0; index < left.Parameters.Length; index++)
            if (left.Parameters[index].RefKind != right.Parameters[index].RefKind ||
                !SameType(left.Parameters[index].Type, right.Parameters[index].Type))
                return false;
        return true;
    }

    private static bool SameType(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITypeParameterSymbol leftParameter && right is ITypeParameterSymbol rightParameter)
            return leftParameter.TypeParameterKind == rightParameter.TypeParameterKind &&
                   leftParameter.Ordinal == rightParameter.Ordinal;
        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
            return leftArray.Rank == rightArray.Rank && SameType(leftArray.ElementType, rightArray.ElementType);
        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
            return SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition) &&
                   leftNamed.TypeArguments.Length == rightNamed.TypeArguments.Length &&
                   leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, SameType).All(equal => equal);
        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static bool IsGuard(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == GuardType) return true;
        return false;
    }

    private static bool HasMarker(IMethodSymbol constructor, string marker) =>
        constructor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == marker);

    private static int? FallbackKind(IMethodSymbol constructor)
    {
        var marker = constructor.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == FallbackMarker);
        return marker is null || marker.ConstructorArguments.Length == 0
            ? null : marker.ConstructorArguments[0].Value as int?;
    }

    private static bool IsDefault(TypedConstant value)
    {
        if (value.IsNull) return true;
        return value.Value switch
        {
            string text => text.Length == 0,
            bool boolean => !boolean,
            char character => character == '\0',
            byte number => number == 0,
            sbyte number => number == 0,
            short number => number == 0,
            ushort number => number == 0,
            int number => number == 0,
            uint number => number == 0,
            long number => number == 0,
            ulong number => number == 0,
            float number => number == 0,
            double number => number == 0,
            _ => false
        };
    }

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor descriptor,
        Location? location, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
}

internal static class ImmutableArrayExtensions
{
    internal static int IndexOf<T>(this ImmutableArray<T> values, System.Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Length; index++)
            if (predicate(values[index])) return index;
        return -1;
    }
}
