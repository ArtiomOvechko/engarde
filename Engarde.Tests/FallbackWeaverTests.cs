using Engarde;
using Engarde.Weaver;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Tests;

public sealed class FallbackWeaverTests
{
    [Theory]
    [InlineData("[Engarde(1, \"flag\")] public sealed class Subject { }")]
    [InlineData("public sealed class Subject { [Engarde(1, \"flag\")] public void Read() { } }")]
    public void Rejects_value_selective_guards_without_a_value_context(string declaration)
    {
        var assembly = Compile("using Engarde; " + declaration);
        AssertWeaverError(assembly, "use it on a field, property, or parameter");
    }

    [Theory]
    [InlineData("[Engarde(typeof(Replacement), \"flag\")] public int Read() => 1;", "type fallbacks can only be applied to classes or interfaces")]
    [InlineData("[Engarde(nameof(Replacement.Read), typeof(Replacement), \"flag\")] public int Read() => 1;", "same generic arity, parameters and return type")]
    public void Rejects_invalid_method_fallbacks(string member, string message)
    {
        var assembly = Compile($$"""
            using Engarde;
            public sealed class Subject { {{member}} }
            public sealed class Replacement { public static string Read() => "replacement"; }
            """);
        AssertWeaverError(assembly, message);
    }

    [Theory]
    [InlineData("[Engarde(nameof(Replacement.Read), typeof(Replacement), \"flag\")]", "method fallbacks can only be applied to methods")]
    [InlineData("[Engarde(typeof(Replacement), \"flag\")]", "must be a concrete class that implements or inherits guarded type")]
    public void Rejects_invalid_type_fallbacks(string attribute, string message)
    {
        var assembly = Compile($$"""
            using Engarde;
            {{attribute}} public class Subject { public virtual int Read() => 1; }
            public sealed class Replacement { public static int Read() => 2; }
            """);
        AssertWeaverError(assembly, message);
    }

    private static void AssertWeaverError(string assembly, string message)
    {
        try
        {
            var error = Assert.Throws<NotSupportedException>(() =>
                new ModuleWeaver(assembly, References()).Execute());
            Assert.Contains(message, error.Message);
        }
        finally
        {
            File.Delete(assembly);
        }
    }

    private static string Compile(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Engarde.Fallback.{Guid.NewGuid():N}.dll");
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source)], References().Select(reference => MetadataReference.CreateFromFile(reference)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static string[] References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(EngardeAttribute).Assembly.Location).Distinct().ToArray();
}
