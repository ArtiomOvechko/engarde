using System.Collections.Immutable;
using Engarde;
using Engarde.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Tests;

public sealed class GuardUsageAnalyzerTests
{
    [Theory]
    [InlineData("[Engarde(1, \"flag\")] public sealed class Subject { }", "EG1004")]
    [InlineData("public sealed class Subject { [Engarde(1, \"flag\")] public void Read() { } }", "EG1004")]
    [InlineData("public sealed class Subject { [Engarde(1, \"flag\")] public string Value { get; set; } = \"\"; }", "EG1007")]
    [InlineData("[Engarde(typeof(Replacement), \"flag\")] public class Subject { } public sealed class Replacement { }", "EG1005")]
    [InlineData("public sealed class Subject { [Engarde(\"Missing\", typeof(Replacement), \"flag\")] public int Read() => 1; } public static class Replacement { }", "EG1006")]
    public async Task Reports_invalid_constructor_intent_during_analysis(string source, string diagnosticId)
    {
        var diagnostics = await AnalyzeAsync("using Engarde; " + source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public async Task Accepts_valid_default_and_generated_guard_constructors()
    {
        var diagnostics = await AnalyzeAsync("""
            using Engarde;
            public enum Pizza { Pineapple = 1, Mushroom = 2 }
            public sealed class Fallback : Subject { public override int Read() => 2; }
            [Engarde(typeof(Fallback), "type-flag")]
            public class Subject
            {
                [Engarde(Pizza.Pineapple, "value-flag")]
                public Pizza Value { get; set; }
                [Engarde(nameof(ReadOld), typeof(Subject), "method-flag")]
                public virtual int Read() => 1;
                private int ReadOld() => 0;
            }
            public sealed partial class PizzaGuardAttribute(params string[] features) : EngardeAttribute(features);
            public sealed class Menu
            {
                [PizzaGuard(Pizza.Pineapple, "generated-value-flag")]
                public Pizza Value { get; set; }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Validates_generated_guard_constructor_intent()
    {
        var diagnostics = await AnalyzeAsync("""
            using Engarde;
            public enum Pizza { Pineapple = 1 }
            public sealed partial class PizzaGuardAttribute(params string[] features) : EngardeAttribute(features);
            public sealed class Menu
            {
                [PizzaGuard(Pizza.Pineapple, "flag")]
                public string Value { get; set; } = "";
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "EG1007");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(EngardeAttribute).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GuardUsageAnalyzerTests",
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new GuardGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
        return await generated.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new GuardUsageAnalyzer())).GetAllDiagnosticsAsync();
    }
}
