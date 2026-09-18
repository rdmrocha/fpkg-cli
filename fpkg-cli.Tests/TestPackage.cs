internal static class TestPackage
{
    internal const string FileName = "Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg";

    /// <summary>Read from the package header via <c>ProsperoPkgReader.Read(...).Header.ContentId</c>.</summary>
    internal const string ContentId = "EP4060-PPSA25872_00-T2DNFMAINGAMEPS5";

    /// <summary>
    /// LibProsperoPkg is referenced with Private=false (see fpkg.Tests.csproj), so it is not
    /// copied into this project's output. fpkg-cli.csproj carries its own resolver
    /// (Fpkg.Cli.LibraryResolver) for this, but that module initializer only runs once
    /// something in the Fpkg.Cli namespace is touched, which no test here does. Mirrors
    /// PprPfsKrakenTool.Tests/TestResolver.cs, reusing RepoRoot instead of a separate locator.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InstallResolver()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string p = System.IO.Path.Combine(RepoRoot, name.Name + ".dll");
            return File.Exists(p) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
        };
    }

    /// <summary>Walks up from the test binary looking for the release folder.</summary>
    internal static string RepoRoot { get; } = Find();

    internal static string Path => System.IO.Path.Combine(RepoRoot, FileName);

    internal static bool Exists => File.Exists(Path);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "LibProsperoPkg.dll")))
                return dir.FullName;
        throw new InvalidOperationException("LibProsperoPkg.dll not found above " + AppContext.BaseDirectory);
    }
}
