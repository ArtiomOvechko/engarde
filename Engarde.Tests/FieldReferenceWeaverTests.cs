using Engarde;
using Engarde.Weaver;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Tests;

public sealed class FieldReferenceWeaverTests
{
    [Theory]
    [InlineData("ref int value = ref subject.Value; value = 1;", "ldflda")]
    [InlineData("Write(ref subject.Value);", "ldflda")]
    [InlineData("ref int value = ref Subject.StaticValue; value = 1;", "ldsflda")]
    public void Rejects_managed_references_to_guarded_fields(string statement, string expectedOpcode)
    {
        var source = $$"""
            using Engarde;
            public sealed class Subject
            {
                [Engarde("flag")] public int Value;
                [Engarde("flag")] public static int StaticValue;
                public static void Update(Subject subject) { {{statement}} }
                private static void Write(ref int value) => value = 1;
            }
            """;
        var assemblyPath = Compile(source);

        try
        {
            var error = Assert.Throws<NotSupportedException>(() =>
                new ModuleWeaver(assemblyPath, References()).Execute());
            Assert.Contains("Cannot take a managed reference to guarded field", error.Message);
            Assert.Contains("assign the field directly or guard the containing method instead", error.Message);
            Assert.Contains(expectedOpcode == "ldsflda" ? "StaticValue" : "Value", error.Message);
        }
        finally
        {
            File.Delete(assemblyPath);
        }
    }

    [Fact]
    public void Still_weaves_direct_writes_to_guarded_fields()
    {
        var assemblyPath = Compile("""
            using Engarde;
            public sealed class Subject
            {
                [Engarde("flag")] public int Value;
                public static void Update(Subject subject) => subject.Value = 1;
            }
            """);

        try
        {
            Assert.Equal(1, new ModuleWeaver(assemblyPath, References()).Execute());
        }
        finally
        {
            File.Delete(assemblyPath);
        }
    }

    private static string Compile(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Engarde.FieldReference.{Guid.NewGuid():N}.dll");
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source)], References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static string[] References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(EngardeAttribute).Assembly.Location).Distinct().ToArray();
}
