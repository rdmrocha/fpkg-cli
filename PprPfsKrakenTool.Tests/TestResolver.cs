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
        // There is no DllImport resolver here any more: the Oodle encoder used to be
        // reached through a C shim (libfpkgoodle.dylib) that needed one, and is now bound
        // directly by PprPfsKrakenTool.OodleLibrary/NativeOodle, which does its own search
        // — including this release folder's native/ — with no P/Invoke by name.
    }
}
