using Mono.Cecil;

namespace Engarde.Weaver;

/// <summary>
/// Resolves assemblies from the exact reference paths the project was compiled against
/// </summary>
internal sealed class ReferenceAssemblyResolver : DefaultAssemblyResolver
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ReferenceAssemblyResolver(IEnumerable<string> referencePaths, string assemblyPath)
    {
        foreach (var path in referencePaths)
            _paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);

        AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        if (_cache.TryGetValue(name.Name, out var cached))
            return cached;

        var assembly = _paths.TryGetValue(name.Name, out var path)
            ? AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this })
            : base.Resolve(name);

        _cache[name.Name] = assembly;

        return assembly;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var assembly in _cache.Values)
            assembly.Dispose();

        base.Dispose(disposing);
    }
}
