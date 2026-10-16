using Engarde.Weaver;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: Engarde.Weaver <assembly> [references file]");
    return 1;
}

try
{
    var references = args.Length == 2
        ? File.ReadAllLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
        : [];

    var woven = new ModuleWeaver(args[0], references).Execute();
    Console.WriteLine($"Engarde: {woven} check(s) woven into {Path.GetFileName(args[0])}");

    return 0;
}
catch (Exception e)
{
    // MSBuild canonical error format, so the build fails with a readable message
    Console.Error.WriteLine($"{args[0]} : error EG0001: Feature guard weaving failed: {e}");
    return 1;
}
