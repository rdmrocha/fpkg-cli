using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class TestResolver
{
    [ModuleInitializer]
    internal static void Install()
    {
        string release = ReleaseFolder.Find();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string p = Path.Combine(release, name.Name + ".dll");
            return File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
        };
    }
}
