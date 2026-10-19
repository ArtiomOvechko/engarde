using Engarde;
using Engarde.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Tests;

public sealed class GeneratorTests
{
    private static CSharpCompilation Compile(string declaration)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(EngardeAttribute).Assembly.Location));
        return CSharpCompilation.Create("GeneratorConsumer",
            [CSharpSyntaxTree.ParseText("using Engarde;\n" + declaration)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static (GeneratorDriverRunResult Result, Compilation Output) Generate(string declaration)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new GuardGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(declaration), out var output, out _);
        return (driver.GetRunResult(), output);
    }

    private static bool WithoutToken(Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.LastOrDefault()?.Type?.ToString() != "global::System.Threading.CancellationToken";

    [Fact]
    public void Every_generated_query_has_a_cancellation_token_overload()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            public sealed class Tenant;
            public sealed partial class LevelGuard : EngardeAttribute<Tenant, string>
            {
                public LevelGuard() : base("level") { }
                public LevelGuard(int cancellationToken) : base("level") { }
                protected override System.Threading.Tasks.ValueTask<string> GetScopeIdAsync(
                    Tenant tenant, System.Threading.CancellationToken cancellationToken) => new("tenant");
            }
            [GenerateFeatureGuards("feature")]
            public partial class PermissionGuard : EngardeAttribute;
            public static class Caller
            {
                public static async System.Threading.Tasks.ValueTask<bool> CheckAsync(System.Threading.CancellationToken token) =>
                    await LevelGuard.CreateInstance().IsFeatureEnabledAsync(1, "tenant", token)
                    && await FeaturePermissionGuard.CreateInstance().IsFeatureEnabledAsync(7, token);
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        foreach (var contract in result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>()))
        {
            var signatures = contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Select(method => method.TypeParameterList + "(" + string.Join(",", method.ParameterList.Parameters) + ")")
                .ToArray();
            var queries = contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Where(WithoutToken).ToArray();
            Assert.Equal(queries.Length * 2, signatures.Length);
            foreach (var query in queries)
            {
                var parameters = query.ParameterList.Parameters.Select(parameter => parameter.ToString()).ToList();
                var token = parameters.Any(parameter => parameter.EndsWith(" cancellationToken")) ? "_cancellationToken" : "cancellationToken";
                Assert.Contains(query.TypeParameterList + "(" + string.Join(",",
                    parameters.Append("global::System.Threading.CancellationToken " + token)) + ")", signatures);
            }
        }
    }

    [Fact]
    public void Generates_scope_id_overloads_for_guards_with_their_own_scope_id_type()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            public sealed class Tenant;
            public abstract class TenantGuard(params string[] featureNames) : EngardeAttribute<Tenant, string>(featureNames)
            {
                protected override System.Threading.Tasks.ValueTask<string> GetScopeIdAsync(Tenant tenant, System.Threading.CancellationToken cancellationToken) => new("tenant");
            }
            public sealed partial class SettingsGuard() : TenantGuard("settings");
            public sealed partial class RuntimeGuard(params string[] featureNames) : TenantGuard(featureNames);
            public sealed partial class PlainGuard() : EngardeAttribute("plain");
            public sealed partial class OverlapGuard : EngardeAttribute<Tenant, int>
            {
                public OverlapGuard() : base("overlap") { }
                public OverlapGuard(int level) : base("overlap") { }
                protected override System.Threading.Tasks.ValueTask<int> GetScopeIdAsync(Tenant tenant, System.Threading.CancellationToken cancellationToken) => new(1);
            }
            public static class Caller
            {
                public static async System.Threading.Tasks.ValueTask<bool> CheckAsync() =>
                    await SettingsGuard.CreateInstance().IsFeatureEnabledAsync("tenant")
                    && await RuntimeGuard.CreateInstance().IsFeatureEnabledAsync("flag", "tenant")
                    && await OverlapGuard.CreateInstance().IsFeatureEnabledAsync(1, 2);
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contracts = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
            .ToDictionary(contract => contract.Identifier.ValueText, contract => contract.Members
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Where(WithoutToken)
                .Select(method => string.Join(",", method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText)))
                .ToArray());
        Assert.Equal(new[] { "", "scopeId" }, contracts["ISettingsGuard"]);
        Assert.Equal(new[] { "featureFlagName", "featureFlagName,scopeId" }, contracts["IRuntimeGuard"]);
        Assert.Equal(new[] { "", "scopeId" }, contracts["IPlainGuard"]);
        // A constructor query takes precedence over a scope overload with the same signature
        Assert.Equal(new[] { "", "level", "level,scopeId" }, contracts["IOverlapGuard"]);
        // A string scope id must not bind to the feature name overload
        Assert.Contains(result.GeneratedTrees, tree => tree.ToString().Contains("IsFeatureEnabledAsync<string>(new global::Consumer.SettingsGuard(), scopeId)"));
    }

    [Fact]
    public void Generates_generic_scope_id_overloads_for_guards_on_the_registered_store()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            [GenerateFeatureGuards("feature")]
            public partial class PermissionGuard : EngardeAttribute;
            [GenerateFeatureGuards("feature")]
            public partial class LevelGuard : EngardeAttribute
            {
                public LevelGuard() { }
                public LevelGuard(int level) { }
            }
            public sealed partial class GenericGuard<TFeatureScopeId>() : EngardeAttribute("generic");
            public sealed partial class StoreGuard() : EngardeAttribute<object>("store")
            {
                protected override System.Threading.Tasks.ValueTask<bool> IsEnabledAsync(object store, string featureName, System.Threading.CancellationToken cancellationToken) => new(true);
            }
            public static class Caller
            {
                public static async System.Threading.Tasks.ValueTask<bool> CheckAsync() =>
                    await FeaturePermissionGuard.CreateInstance().IsFeatureEnabledAsync(7)
                    && await FeatureLevelGuard.CreateInstance().IsFeatureEnabledAsync(1, "tenant")
                    && await GenericGuard<int>.CreateInstance().IsFeatureEnabledAsync(7L);
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contracts = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
            .ToDictionary(contract => contract.Identifier.ValueText, contract => contract.Members
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Where(WithoutToken)
                .Select(method => method.TypeParameterList + "(" + string.Join(",", method.ParameterList.Parameters.Select(parameter => parameter.ToString())) + ")")
                .ToArray());
        Assert.Equal(new[] { "()", "<TFeatureScopeId>(TFeatureScopeId scopeId)" }, contracts["IFeaturePermissionGuard"]);
        // A level argument would take the scope id of a parameterless generic overload, so only the level one is generated
        Assert.Equal(new[] { "()", "(int level)", "<TFeatureScopeId>(int level,TFeatureScopeId scopeId)" }, contracts["IFeatureLevelGuard"]);
        Assert.Equal(new[] { "()", "<_TFeatureScopeId>(_TFeatureScopeId scopeId)" }, contracts["IGenericGuard"]);
        // A guard with its own store has no feature scope to override
        Assert.Equal(new[] { "()" }, contracts["IStoreGuard"]);
    }

    [Fact]
    public async Task Suppresses_attribute_conventions_for_guards_only()
    {
        var compilation = Compile("""
            public sealed class AccessGuard() : EngardeAttribute("access");
            public abstract class TenantGuard() : EngardeAttribute<object, string>("tenant");
            [GenerateFeatureGuards("feature")]
            public class TemplateGuard : EngardeAttribute;
            public class ForwardingGuard(params string[] featureNames) : EngardeAttribute(featureNames);
            public class OpenGuard() : EngardeAttribute("open");
            public class Marker : System.Attribute;
            """);
        var diagnostics = await compilation.WithAnalyzers(
                [new AttributeConventionAnalyzer(), new GuardDiagnosticSuppressor()],
                new CompilationWithAnalyzersOptions(
                    new AnalyzerOptions([]), null, false, false, reportSuppressedDiagnostics: true))
            .GetAnalyzerDiagnosticsAsync();

        var reported = diagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage().Split(' ')[0]} {diagnostic.IsSuppressed}");
        Assert.Equal(new[]
        {
            "S3376 AccessGuard True", "S3993 AccessGuard True",
            "S3376 TenantGuard True", "S3993 TenantGuard True",
            "S3376 TemplateGuard True", "S3993 TemplateGuard True", "S4060 TemplateGuard True",
            "S3376 ForwardingGuard True", "S3993 ForwardingGuard True", "S4060 ForwardingGuard True",
            // Unsealed without being a template or a forwarding base: the rule still applies
            "S3376 OpenGuard True", "S3993 OpenGuard True", "S4060 OpenGuard False",
            "S3376 Marker False", "S3993 Marker False", "S4060 Marker False"
        }.Order(), reported.Order());
    }

    // Stands in for SonarAnalyzer's S3376 (Attribute suffix), S3993 (AttributeUsage) and S4060 (unsealed attribute)
    // rules; it only runs in this test
#pragma warning disable RS1036, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class AttributeConventionAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Suffix = new("S3376", "Suffix", "{0} needs the Attribute suffix", "Naming", DiagnosticSeverity.Warning, true);
        private static readonly DiagnosticDescriptor Usage = new("S3993", "Usage", "{0} needs AttributeUsage", "Design", DiagnosticSeverity.Warning, true);
        private static readonly DiagnosticDescriptor Sealed = new("S4060", "Sealed", "{0} should be sealed or abstract", "Performance", DiagnosticSeverity.Warning, true);

        public override System.Collections.Immutable.ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Suffix, Usage, Sealed];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(symbol =>
            {
                var type = (INamedTypeSymbol)symbol.Symbol;
                for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
                {
                    if (parent.ToDisplayString() != "System.Attribute") continue;
                    symbol.ReportDiagnostic(Diagnostic.Create(Suffix, type.Locations[0], type.Name));
                    symbol.ReportDiagnostic(Diagnostic.Create(Usage, type.Locations[0], type.Name));
                    if (!type.IsSealed && !type.IsAbstract)
                        symbol.ReportDiagnostic(Diagnostic.Create(Sealed, type.Locations[0], type.Name));
                }
            }, SymbolKind.NamedType);
        }
    }
#pragma warning restore RS1036, RS2008

    [Fact]
    public void Generates_compilable_overloads_for_primary_and_regular_constructors()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            public partial class Guard(int @event, params string[] featureNames) : EngardeAttribute(featureNames);
            public sealed partial class FixedGuard(int id) : EngardeAttribute("fixed");
            public partial class SingleFlagGuard(string featureName) : EngardeAttribute(featureName);
            public sealed partial class DefaultGuard : EngardeAttribute;
            public sealed partial class PoolGuard(string featurePool) : EngardeAttribute("fixed");
            public sealed partial class Outer<T> where T : struct
            {
                public partial class Nested<U>(U? setting, params string[] features) : EngardeAttribute(features) where U : class;
            }
            public static class Caller
            {
                public static async System.Threading.Tasks.ValueTask<bool> CheckAsync() => await Guard.CreateInstance().IsFeatureEnabledAsync(1, "flag")
                    && await FixedGuard.CreateInstance().IsFeatureEnabledAsync(2) && await SingleFlagGuard.CreateInstance().IsFeatureEnabledAsync("flag") && await DefaultGuard.CreateInstance().IsFeatureEnabledAsync()
                    && await Outer<int>.Nested<string>.CreateInstance().IsFeatureEnabledAsync(null, "flag") && await PoolGuard.CreateInstance().IsFeatureEnabledAsync("pool");
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(7, result.GeneratedTrees.Length);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var guardTrees = result.GeneratedTrees.Where(tree => !tree.FilePath.EndsWith("Engarde.GeneratedQueryRegistrations.g.cs")).ToArray();
        foreach (var tree in guardTrees)
        {
            var methods = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == "IsFeatureEnabledAsync").ToArray();
            Assert.NotEmpty(methods);
            Assert.All(methods, method =>
            {
                Assert.Equal("IsFeatureEnabledAsync", method.Identifier.ValueText);
                Assert.Equal("global::System.Threading.Tasks.ValueTask<bool>", method.ReturnType.ToString());
                Assert.DoesNotContain(method.Modifiers, modifier => modifier.ValueText == "static");
            });
            Assert.Single(tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>());
        }
        var registrations = result.GeneratedTrees.Single(tree => tree.FilePath.EndsWith("Engarde.GeneratedQueryRegistrations.g.cs")).ToString();
        Assert.Contains("typeof(global::Consumer.IGuard)", registrations);
        Assert.Contains("typeof(global::Consumer.Guard.__EngardeQueries)", registrations);
        Assert.Contains("ModuleInitializer", registrations);
    }

    [Fact]
    public void Generates_exactly_one_bound_or_unbound_query_per_constructor()
    {
        var (result, output) = Generate("""
            public partial class UnboundGuard(int setting, params string[] features) : EngardeAttribute(features);
            public sealed partial class BoundGuard(int setting) : EngardeAttribute("fixed");
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contracts = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
            .ToDictionary(contract => contract.Identifier.ValueText);
        var unbound = Assert.Single(contracts["IUnboundGuard"].Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(), method => method.TypeParameterList is null && WithoutToken(method));
        Assert.Equal(new[] { "setting", "featureFlagName" }, unbound.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText));
        var bound = Assert.Single(contracts["IBoundGuard"].Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(), method => method.TypeParameterList is null && WithoutToken(method));
        Assert.Equal(new[] { "setting" }, bound.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText));
    }

    [Fact]
    public void Does_not_expose_less_accessible_constructors_through_public_helpers()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            internal sealed class InternalSetting;
            public sealed partial class Guard : EngardeAttribute
            {
                public Guard() : base("fixed") { }
                private Guard(InternalSetting setting) : base("fixed") { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contract = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
            .Single(type => type.Identifier.ValueText == "IGuard");
        var method = Assert.Single(contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.TypeParameterList is null && WithoutToken(method));
        Assert.Empty(method.ParameterList.Parameters);
    }

    [Fact]
    public void Does_not_generate_query_helpers_when_a_public_guard_has_only_a_private_constructor()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            internal sealed class InternalSetting;
            public sealed partial class Guard : EngardeAttribute
            {
                private Guard(InternalSetting setting) : base("fixed") { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(result.GeneratedTrees, tree => tree.ToString().Contains("interface IGuard"));
    }

    [Fact]
    public void Feature_bound_generation_does_not_expose_less_accessible_template_constructors()
    {
        var (result, output) = Generate("""
            namespace Consumer;
            internal sealed class InternalSetting;
            [GenerateFeatureGuards("feature")]
            public partial class PermissionGuard : EngardeAttribute
            {
                public PermissionGuard() { }
                internal PermissionGuard(InternalSetting setting) { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contract = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
            .Single(type => type.Identifier.ValueText == "IFeaturePermissionGuard");
        var method = Assert.Single(contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.TypeParameterList is null && WithoutToken(method));
        Assert.Empty(method.ParameterList.Parameters);
        var generatedGuard = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            .Single(type => type.Identifier.ValueText == "FeaturePermissionGuard");
        Assert.Single(generatedGuard.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>());
    }

    [Fact]
    public void Generates_sealed_feature_bound_guards_from_an_inheritable_template()
    {
        var (result, output) = Generate("""
            [GenerateFeatureGuards("feature1", "feature-two")]
            public partial class PermissionGuard(int level) : EngardeAttribute;
            public static class Consumer
            {
                public static IFeature1PermissionGuard First => Feature1PermissionGuard.CreateInstance();
                public static IFeatureTwoPermissionGuard Second => FeatureTwoPermissionGuard.CreateInstance();
                public static async System.Threading.Tasks.ValueTask<bool> Check() =>
                    await First.IsFeatureEnabledAsync(1) && await Second.IsFeatureEnabledAsync(2);
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(3, result.GeneratedTrees.Length);
        var classes = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
            .Where(type => type.Identifier.ValueText is "Feature1PermissionGuard" or "FeatureTwoPermissionGuard")
            .ToDictionary(type => type.Identifier.ValueText);
        Assert.Equal(2, classes.Count);
        Assert.All(classes.Values, type => Assert.Contains(type.Modifiers, modifier => modifier.ValueText == "sealed"));
        foreach (var interfaceName in new[] { "IFeature1PermissionGuard", "IFeatureTwoPermissionGuard" })
        {
            var contract = result.GeneratedTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
                .Single(type => type.Identifier.ValueText == interfaceName);
            var method = Assert.Single(contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.TypeParameterList is null && WithoutToken(method));
            Assert.Equal("int level", Assert.Single(method.ParameterList.Parameters).ToString());
        }
    }

    [Fact]
    public void Rejects_a_user_written_feature_bound_guard_that_is_not_sealed()
    {
        var (result, _) = Generate("public partial class FixedGuard : EngardeAttribute(\"fixed\");");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("EG1003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("must be sealed", diagnostic.GetMessage());
    }

    [Fact]
    public void Generates_default_IEngarde_for_the_core_compilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Engarde.Core",
            [CSharpSyntaxTree.ParseText("""
                namespace Engarde;
                public sealed class Engarde { }
                public class EngardeAttribute : System.Attribute { }
                """)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new GuardGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var tree = Assert.Single(driver.GetRunResult().GeneratedTrees);
        Assert.Equal("IEngarde.g.cs", Path.GetFileName(tree.FilePath));
        var contract = tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>().Single();
        var method = Assert.Single(contract.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>(),
            method => method.TypeParameterList is null && WithoutToken(method));
        Assert.Equal("IEngarde", contract.Identifier.ValueText);
        Assert.Equal("IsFeatureEnabledAsync", method.Identifier.ValueText);
        Assert.Equal("string featureFlagName", Assert.Single(method.ParameterList.Parameters).ToString());
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public partial class Guard : EngardeAttribute { public Guard(int id, string featureName) : base(featureName) { } public Guard(int id, params string[] features) : base(features) { } }")]
    [InlineData("public sealed partial class Guard : EngardeAttribute { public Guard(ref int id) { } }")]
    [InlineData("public class Outer { public sealed partial class Guard : EngardeAttribute { } }")]
    public void Reports_actionable_diagnostic_for_unsupported_shapes(string declaration)
    {
        var (result, _) = Generate(declaration);
        Assert.Contains(result.Diagnostics, d => d.Id == "EG1001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generates_private_nested_generic_interfaces_with_matching_constraints()
    {
        var (result, output) = Generate("""
            public partial class Outer<T> where T : class, System.IDisposable, new()
            {
                private sealed partial class Hidden<U>(U setting) : EngardeAttribute("fixed") where U : unmanaged;
                public static System.Threading.Tasks.ValueTask<bool> Query() => Hidden<int>.CreateInstance().IsFeatureEnabledAsync(7);
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var contract = Assert.Single(result.GeneratedTrees).GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>().Single();
        Assert.Equal("IHidden", contract.Identifier.ValueText);
        Assert.Contains(contract.Modifiers, modifier => modifier.ValueText == "private");
        Assert.Equal("where U : unmanaged", Assert.Single(contract.ConstraintClauses).ToString());
    }

    [Fact]
    public void Derived_partial_guards_get_their_own_interfaces_and_factory_return_types()
    {
        var (result, output) = Generate("""
            public partial class ParentGuard(params string[] features) : EngardeAttribute(features);
            public sealed partial class ChildGuard() : ParentGuard("fixed");
            public class Consumer
            {
                public IChildGuard Query => ChildGuard.CreateInstance();
                public IParentGuard Parent => ParentGuard.CreateInstance();
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Id == "CS0108");
    }

    [Theory]
    [InlineData("ref", "int")]
    [InlineData("in", "int")]
    [InlineData("out", "int")]
    [InlineData("ref", "object")]
    [InlineData("in", "object")]
    [InlineData("out", "object")]
    [InlineData("ref readonly", "object")]
    public void Rejects_guarded_by_reference_parameters(string modifier, string type)
    {
        var body = modifier == "out" ? "value = default;" : "";
        var declaration = $$"""
            public class Consumer
            {
                public void Update([Engarde("flag")] {{modifier}} {{type}} value) { {{body}} }
            }
            """;

        var (result, output) = Generate(declaration);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("EG1002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("'value'", diagnostic.GetMessage());
        Assert.Contains("pass it by value or guard the method instead", diagnostic.GetMessage());
        Assert.Equal("Engarde(\"flag\")", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public interface Consumer { void Update([Guard] ref int value); }")]
    [InlineData("public abstract class Consumer { public abstract void Update([Guard] in object value); }")]
    [InlineData("public class Consumer { public Consumer([Guard] out int value) { value = 0; } }")]
    [InlineData("public delegate void Consumer([Guard] ref object value);")]
    [InlineData("public class Consumer { public void Update<T>([Guard] ref T value) { } }")]
    [InlineData("public class Consumer { public void Update() { void Local([Guard] ref int value) { } } }")]
    [InlineData("public class Consumer { public delegate void Callback(ref int value); public Callback Update = ([Guard] ref int value) => { }; }")]
    public void Rejects_derived_guards_across_parameter_declarations(string declaration)
    {
        var (result, output) = Generate("""
            public class BaseGuard : EngardeAttribute;
            public class Guard : BaseGuard;
            """ + declaration);

        Assert.Equal("EG1002", Assert.Single(result.Diagnostics).Id);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Allows_by_value_reference_types_and_unguarded_by_reference_parameters()
    {
        var (result, output) = Generate("""
            public class Consumer
            {
                [Engarde("flag")] public object Value { get; set; }
                public void Update([Engarde("flag")] object value, [Engarde("flag")] int number) { }
                [Engarde("flag")]
                public void Update(ref object value, in int input, out int output) { output = input; }
                public void Other([System.Runtime.InteropServices.In] ref int value) { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Leaves_existing_non_partial_and_abstract_guards_unchanged()
    {
        var (result, _) = Generate("""
            public class ExistingGuard : EngardeAttribute;
            public abstract partial class AbstractGuard : EngardeAttribute;
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GeneratedTrees);
    }
}
